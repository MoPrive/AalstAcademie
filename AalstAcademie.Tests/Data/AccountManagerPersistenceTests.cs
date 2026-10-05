using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace AalstAcademie.Tests.Data;

public class AccountManagerPersistenceTests
{
    [Theory]
    [InlineData("NullableManagerFk")]
    [InlineData("NullableResponsibleFk")]
    [InlineData("InvalidManagerFkRejected")]
    [InlineData("InvalidResponsibleFkRejected")]
    [InlineData("ManagerDeleteRestrict")]
    [InlineData("ResponsibleDeleteRestrict")]
    [InlineData("ManagerSelfReference")]
    [InlineData("SameHeadMultipleDepartments")]
    [InlineData("DepartmentWorkFkUnchanged")]
    [InlineData("Exactly17DomainAndIdentityTables")]
    public async Task Real_sqlite_relations(string variant)
    {
        await using var f = await EnrolmentFixture.CreateAsync();
        if (variant == "InvalidManagerFkRejected")
        { await Assert.ThrowsAsync<DbUpdateException>(() => f.MutateAsync(async db => (await db.Users.SingleAsync(x => x.Id == f.Employee.Id)).ManagerUserId = "absent")); return; }
        if (variant == "InvalidResponsibleFkRejected")
        { await Assert.ThrowsAsync<DbUpdateException>(() => f.MutateAsync(async db => (await db.Departments.FirstAsync()).ResponsibleUserId = "absent")); return; }
        if (variant is "ManagerDeleteRestrict" or "ResponsibleDeleteRestrict")
        {
            // Een verder ongerefereerde head voorkomt dat een andere domein-FK een vals positief deletebewijs geeft.
            var head = await f.OtherEmployeeAsync();
            await f.MutateAsync(async db => { if (variant == "ManagerDeleteRestrict") (await db.Users.SingleAsync(x => x.Id == f.Employee.Id)).ManagerUserId = head.Id;
                else (await db.Departments.FirstAsync()).ResponsibleUserId = head.Id; });
            await Assert.ThrowsAsync<DbUpdateException>(() => f.MutateAsync(async db => db.Users.Remove(await db.Users.SingleAsync(x => x.Id == head.Id)))); return;
        }
        if (variant == "ManagerSelfReference") await f.MutateAsync(async db => (await db.Users.SingleAsync(x => x.Id == f.Employee.Id)).ManagerUserId = f.Employee.Id);
        if (variant == "SameHeadMultipleDepartments") await f.MutateAsync(async db => { foreach (var d in await db.Departments.Take(2).ToListAsync()) d.ResponsibleUserId = f.Owner.Id; });
        if (variant == "DepartmentWorkFkUnchanged")
        { var prior = f.Employee.DepartmentId; await f.LinkHeadAsync(); Assert.Equal(prior, (await f.Context.Users.AsNoTracking().SingleAsync(x => x.Id == f.Employee.Id)).DepartmentId); }
        if (variant == "NullableManagerFk") Assert.Null((await f.Context.Users.AsNoTracking().SingleAsync(x => x.Id == f.Employee.Id)).ManagerUserId);
        if (variant == "NullableResponsibleFk") Assert.All(await f.Context.Departments.AsNoTracking().ToListAsync(), x => Assert.Null(x.ResponsibleUserId));
        if (variant == "ManagerSelfReference") Assert.Equal(f.Employee.Id, (await f.Context.Users.AsNoTracking().SingleAsync(x => x.Id == f.Employee.Id)).ManagerUserId);
        if (variant == "SameHeadMultipleDepartments") Assert.Equal(2, await f.Context.Departments.CountAsync(x => x.ResponsibleUserId == f.Owner.Id));
        if (variant == "Exactly17DomainAndIdentityTables")
        { await f.Context.Database.OpenConnectionAsync(); await using var cmd = f.Context.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '__EF%'";
            Assert.Equal(17L, Convert.ToInt64(await cmd.ExecuteScalarAsync())); }
        Assert.True(f.Context.Model.FindEntityType(typeof(Department))!.FindProperty("ResponsibleUserId")!.IsConcurrencyToken);
    }
}
