// Sprint 003: Toetst de echte SQLite-constraints en restrictieve verwijzingen van zaal en moment
// Opnieuw lezen bewijst behoud van datum en volledige tijdticks.
using System.Text;
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TrainingEntity = AalstAcademie.Web.Models.Domain.Training;

namespace AalstAcademie.Tests.Data;

/// <summary>Acht echte providergevallen bewijzen zaal/moment-FKs, scalargrenzen en volledige tickprecisie.</summary>
public class LocationMomentPersistenceTests
{
    // CLR-datum/tijd/status moeten dezelfde betekenis en precisie houden na een onafhankelijke database-read.
    [Fact]
    public async Task Typed_date_ticks_and_both_statuses_roundtrip()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var moment = await PrepareAsync(db);
        foreach (var status in new[] { TrainingMomentStatus.Scheduled, TrainingMomentStatus.Cancelled })
        {
            db.Context.ChangeTracker.Clear();
            var tracked = await db.Context.TrainingMoments.SingleAsync(x => x.Id == moment.Id);
            tracked.Status = status;
            await db.Context.SaveChangesAsync();
            var stored = await db.Context.TrainingMoments.AsNoTracking().SingleAsync(x => x.Id == moment.Id);
            Assert.Equal(new DateOnly(2027, 6, 15), stored.Date);
            Assert.Equal(new TimeOnly(9, 15, 30).Ticks + 1234567, stored.StartTime.Ticks);
            Assert.Equal(new TimeOnly(11, 45, 15).Ticks + 7654321, stored.EndTime.Ticks);
            Assert.Equal(status, stored.Status);
            Assert.Equal(moment.TrainingId, stored.TrainingId);
            Assert.Equal(moment.LocationId, stored.LocationId);
        }
    }

    // SQLite HasMaxLength alleen is geen fysieke grens; ook directe schrijvers krijgen een constraintfout.
    [Fact]
    public async Task Location_text_and_positive_integer_capacity_are_guarded()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var moment = await PrepareAsync(db);
        foreach (var value in new object?[] { null, " ", new string('x', 201), "zaal\0achter" })
            await RejectAsync(db, "Locations", moment.LocationId, "Name", value);
        foreach (var address in new[] { new string('x', 501), "adres\0suffix", "adres\0" + new string('x', 501) })
            await RejectAsync(db, "Locations", moment.LocationId, "Address", address);
        foreach (var value in new object?[] { null, 0, -1, 1.5, "invalid" })
            await RejectAsync(db, "Locations", moment.LocationId, "MaximumCapacity", value);
        var stored = await db.Context.Locations.AsNoTracking().SingleAsync();
        Assert.Equal("Fictieve zaal", stored.Name);
        Assert.Equal(32, stored.MaximumCapacity);
        // Twee werkelijk verschillende locaties mogen dezelfde naam hebben: geen ongevraagde unieke naam.
        db.Context.Locations.Add(new Location { Name = stored.Name, MaximumCapacity = 1 });
        await db.Context.SaveChangesAsync();
        Assert.Equal(2, await db.Context.Locations.CountAsync());
    }

    // Lokale checks bewaken vorm/ticks/volgorde/enum, inclusief een datum met geldig prefix maar NUL-suffix.
    // Kalender, DST en cross-roomcapaciteit zijn servicewerk.
    [Fact]
    public async Task Moment_scalar_tick_and_status_checks_reject_raw_invalid_values()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var moment = await PrepareAsync(db);
        foreach (var value in new object?[] { null, "15/06/2027", "not-a-date", "2027-06-15\0suffix" })
            await RejectAsync(db, "TrainingMoments", moment.Id, "Date", value);
        foreach (var value in new object[] { -1L, 864000000000L, 12.5 })
            await RejectAsync(db, "TrainingMoments", moment.Id, "StartTimeTicks", value);
        foreach (var value in new object[] { 0L, moment.StartTime.Ticks, 864000000000L, 12.5 })
            await RejectAsync(db, "TrainingMoments", moment.Id, "EndTimeTicks", value);
        foreach (var value in new object[] { 0, -1, 1.5, "invalid" })
            await RejectAsync(db, "TrainingMoments", moment.Id, "MaximumParticipants", value);
        foreach (var value in new object[] { 0, 3, -1, 1.5, "invalid" })
            await RejectAsync(db, "TrainingMoments", moment.Id, "Status", value);
        var stored = await db.Context.TrainingMoments.AsNoTracking().SingleAsync();
        Assert.Equal(moment.StartTime.Ticks, stored.StartTime.Ticks);
        Assert.Equal(moment.EndTime.Ticks, stored.EndTime.Ticks);
        Assert.Equal(TrainingMomentStatus.Scheduled, stored.Status);
    }

    // De momentverwijzing naar Training én Location is werkelijk verplicht en fysiek afgedwongen.
    [Theory]
    [InlineData("TrainingId")]
    [InlineData("LocationId")]
    public async Task Missing_moment_parent_is_rejected(string column)
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var moment = await PrepareAsync(db);
        var error = await RejectAsync(db, "TrainingMoments", moment.Id, column, 999999);
        Assert.Equal(787, error.SqliteExtendedErrorCode);
        await RejectAsync(db, "TrainingMoments", moment.Id, column, null);
        var stored = await db.Context.TrainingMoments.AsNoTracking().SingleAsync();
        Assert.Equal(moment.TrainingId, stored.TrainingId);
        Assert.Equal(moment.LocationId, stored.LocationId);
    }

    // Tijd of annulering verandert referentie-integriteit niet: alle bestaande momenten verhinderen parentdelete.
    [Theory]
    [InlineData("Locations")]
    [InlineData("Trainings")]
    public async Task Any_moment_reference_restricts_parent_delete(string table)
    {
        if (table is not ("Locations" or "Trainings")) throw new ArgumentOutOfRangeException(nameof(table));
        // De theory gebruikt twee vaste tabelnamen; de expliciete guard houdt SQL-identifiers lokaal begrensd.
        var sql = $"DELETE FROM {table} WHERE Id=@id";
        await using var db = await SqliteTestDatabase.CreateAsync();
        var moment = await PrepareAsync(db);
        foreach (var variant in new[] { "future", "past", "cancelled" })
        {
            var tracked = await db.Context.TrainingMoments.SingleAsync();
            tracked.Date = variant == "future" ? new DateOnly(2027, 6, 15) : new DateOnly(2020, 1, 15);
            tracked.Status = variant == "cancelled" ? TrainingMomentStatus.Cancelled : TrainingMomentStatus.Scheduled;
            await db.Context.SaveChangesAsync();
            var id = table == "Locations" ? moment.LocationId : moment.TrainingId;
            var error = await Assert.ThrowsAsync<SqliteException>(() => db.Context.Database.ExecuteSqlRawAsync(
                sql, new SqliteParameter("@id", id)));
            Assert.Equal(19, error.SqliteErrorCode);
            Assert.Single(await db.Context.TrainingMoments.AsNoTracking().ToListAsync());
        }
    }

    // Fysieke types en versies worden onafhankelijk van een gelijk ogende CLR-readback gecontroleerd.
    [Fact]
    public async Task Guid_and_integer_provider_types_are_exact()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var moment = await PrepareAsync(db);
        foreach (var table in new[] { "Locations", "TrainingMoments" })
            foreach (var invalid in new object?[] { null, "", "bad-guid", Guid.Empty.ToString(), Guid.NewGuid().ToString() + "\0suffix" })
                await RejectAsync(db, table, table == "Locations" ? moment.LocationId : moment.Id, "Version", invalid);
        await using var command = db.Context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT typeof(Date),typeof(StartTimeTicks),typeof(EndTimeTicks),typeof(MaximumParticipants),typeof(Status),typeof(Version),Version FROM TrainingMoments WHERE Id=@id";
        command.Parameters.Add(new SqliteParameter("@id", moment.Id));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("text", reader.GetString(0));
        for (var index = 1; index <= 4; index++) Assert.Equal("integer", reader.GetString(index));
        Assert.Equal("text", reader.GetString(5));
        Assert.Equal(moment.Version, Guid.Parse(reader.GetString(6)));
    }

    /// <summary>Opbouw via gewone EF-rijen is testdata, geen alternatieve publieke momentaanmaakflow.</summary>
    private static async Task<TrainingMoment> PrepareAsync(SqliteTestDatabase db)
    {
        var owner = await db.AddUserAsync(RequestedAccountType.InternalInstructor);
        var category = new Category { Name = $"Momentcategorie {Guid.NewGuid():N}" };
        db.Context.Categories.Add(category);
        db.Context.Entry(category).Property<string>("NormalizedName").CurrentValue = category.Name.ToUpperInvariant();
        db.Context.Entry(category).Property<Guid>("Version").CurrentValue = Guid.NewGuid();
        var training = new TrainingEntity { Title = "Fictieve definitie", Category = category, InstructorUserId = owner.Id };
        var location = new Location { Name = "Fictieve zaal", Address = "Gewone adresregel", MaximumCapacity = 32 };
        var moment = new TrainingMoment { Training = training, Location = location, Date = new DateOnly(2027, 6, 15),
            StartTime = new TimeOnly(9, 15, 30).Add(TimeSpan.FromTicks(1234567)),
            EndTime = new TimeOnly(11, 45, 15).Add(TimeSpan.FromTicks(7654321)), MaximumParticipants = 16 };
        db.Context.TrainingMoments.Add(moment);
        await db.Context.SaveChangesAsync();
        return moment;
    }

    private static async Task<SqliteException> RejectAsync(SqliteTestDatabase db, string table, int id, string column, object? value)
    {
        var allowed = table == "Locations" ? new[] { "Name", "Address", "MaximumCapacity", "Version" }
            : table == "TrainingMoments" ? new[] { "Date", "StartTimeTicks", "EndTimeTicks", "MaximumParticipants", "Status", "Version", "TrainingId", "LocationId" } : [];
        if (!allowed.Contains(column, StringComparer.Ordinal)) throw new ArgumentOutOfRangeException(nameof(column));
        // Alleen NUL-tekst gaat als volledige UTF-8-bytebuffer binnen: gewone tekstbinding kan het suffix verliezen.
        // Een onafhankelijke hex-readback bewijst eerst dat SQLite echt de volledige ongeldige tekst ontvangt.
        var valueParameter = new SqliteParameter("@value", value ?? DBNull.Value);
        var valueExpression = "@value";
        if (value is string nulText && nulText.Contains('\0'))
        {
            var bytes = Encoding.UTF8.GetBytes(nulText);
            valueParameter = new SqliteParameter("@value", SqliteType.Blob) { Value = bytes };
            Assert.Equal(SqliteType.Blob, valueParameter.SqliteType);
            await using var probe = db.Context.Database.GetDbConnection().CreateCommand();
            probe.CommandText = "SELECT typeof(@value), typeof(CAST(@value AS TEXT)), hex(CAST(@value AS TEXT))";
            probe.Parameters.Add(new SqliteParameter("@value", SqliteType.Blob) { Value = bytes });
            await using var reader = await probe.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("blob", reader.GetString(0));
            Assert.Equal("text", reader.GetString(1));
            Assert.Equal(Convert.ToHexString(bytes), reader.GetString(2));
            valueExpression = "CAST(@value AS TEXT)";
        }
        // Identifiers komen uit de allowlist; de SQL-expressie heeft twee vaste vormen en alle waarden blijven parameters.
        var sql = $"UPDATE {table} SET {column}={valueExpression} WHERE Id=@id";
        var error = await Assert.ThrowsAsync<SqliteException>(() => db.Context.Database.ExecuteSqlRawAsync(
            sql, valueParameter, new SqliteParameter("@id", id)));
        Assert.Equal(19, error.SqliteErrorCode);
        return error;
    }
}
