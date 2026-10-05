using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Services.Enrolment;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AalstAcademie.Tests.Enrolment;

public class EmployeeCatalogueQueriesTests
{
    [Theory]
    [InlineData("AllAndSelectedOnly")]
    [InlineData("WrongTargetKnownDetailForbidden")]
    [InlineData("MissingMomentNotFound")]
    [InlineData("FutureOnlyNotCancelled")]
    [InlineData("CategoryFilter")]
    [InlineData("DateStartIdOrder")]
    [InlineData("Page20AndSecondPage")]
    [InlineData("CurrentOwnerRoomAndExternalPrice")]
    [InlineData("EncodedPlainTextDtoNoParticipantContacts")]
    [InlineData("AvailabilityMatchesPriorityRules")]
    public async Task Read_current_own_offer(string variant)
    {
        await using var f = await EnrolmentFixture.CreateAsync(external: variant == "CurrentOwnerRoomAndExternalPrice");
        var queries = f.Services.GetRequiredService<EmployeeCatalogueQueries>();
        if (variant is "WrongTargetKnownDetailForbidden" or "AllAndSelectedOnly")
            await f.MutateAsync(async db => (await db.Trainings.SingleAsync()).AudienceScope = TrainingAudienceScope.Unassigned);
        if (variant == "FutureOnlyNotCancelled")
            await f.MutateAsync(async db => (await db.TrainingMoments.SingleAsync()).Status = TrainingMomentStatus.Cancelled);
        if (variant is "DateStartIdOrder" or "Page20AndSecondPage")
            for (var i = 1; i <= (variant == "Page20AndSecondPage" ? 21 : 3); i++)
                await TrainingTestData.CreateMomentAsync(f.Services, f.Training, location: f.Room, date: f.Moment.Date.AddDays(i));
        if (variant == "CurrentOwnerRoomAndExternalPrice") await f.MutateAsync(async db =>
        { (await db.Locations.SingleAsync()).Name = "Actuele zaal"; (await db.ExternalInstructors.SingleAsync()).OrganizationName = "Actuele academie"; });
        if (variant == "EncodedPlainTextDtoNoParticipantContacts") await f.MutateAsync(async db => (await db.Trainings.SingleAsync()).Description = "<script>tekst</script>");
        if (variant == "AvailabilityMatchesPriorityRules") await f.BufferAsync(await f.OtherEmployeeAsync());
        var before = await f.DigestAsync();
        var list = await queries.GetAsync(f.Employee.Id, variant == "CategoryFilter" ? -1 : null, variant == "Page20AndSecondPage" ? 2 : 1);
        Assert.Equal(TrainingOperationStatus.Success, list.Status);
        if (variant is "AllAndSelectedOnly" or "FutureOnlyNotCancelled" or "CategoryFilter") Assert.Empty(list.Rows!);
        if (variant == "AllAndSelectedOnly")
        {
            await f.MutateAsync(async db => { (await db.Trainings.SingleAsync()).AudienceScope = TrainingAudienceScope.SelectedDepartments;
                db.TrainingDepartments.Add(new() { TrainingId = f.Training.Id, DepartmentId = f.Employee.DepartmentId!.Value }); });
            Assert.Single((await queries.GetAsync(f.Employee.Id)).Rows!); before = await f.DigestAsync();
        }
        if (variant == "DateStartIdOrder") Assert.Equal(list.Rows!.OrderBy(x => x.Date).ThenBy(x => x.StartTime).ThenBy(x => x.MomentId), list.Rows);
        if (variant == "Page20AndSecondPage") { Assert.Equal(22, list.Total); Assert.Equal(2, list.TotalPages); Assert.Equal(2, list.Rows!.Count); }
        var detail = await queries.GetMomentAsync(f.Employee.Id, variant == "MissingMomentNotFound" ? -1 : f.Moment.Id);
        if (variant == "MissingMomentNotFound") Assert.Equal(TrainingOperationStatus.NotFound, detail.Status);
        if (variant == "WrongTargetKnownDetailForbidden") Assert.Equal(TrainingOperationStatus.Forbidden, detail.Status);
        if (variant == "CurrentOwnerRoomAndExternalPrice")
        { Assert.Equal("Actuele zaal", detail.Moment!.Row.Location); Assert.Equal("Actuele academie", detail.Moment.Row.InstructorName); Assert.Equal(f.Training.ExternalTotalPriceEuros, detail.Moment.Row.ExternalTotalPriceEuros); }
        if (variant == "EncodedPlainTextDtoNoParticipantContacts")
        { Assert.Equal("<script>tekst</script>", detail.Moment!.Description); Assert.DoesNotContain(typeof(AalstAcademie.Web.Models.Enrolment.EmployeeCatalogueRow).GetProperties(), p => p.Name.Contains("Email") || p.Name.Contains("Phone")); }
        if (variant == "AvailabilityMatchesPriorityRules") { Assert.Equal(1, detail.Moment!.Row.EligibleWaiters); Assert.Equal(1, detail.Moment.Row.DirectAvailable); }
        Assert.Equal(before, await f.DigestAsync());
    }
}
