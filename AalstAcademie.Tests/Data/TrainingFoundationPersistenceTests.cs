// Sprint 003: Bewijst statuswaarden, UTC-opslag, restrictieve verwijzingen en gedeeltelijke unieke indexen voor echte registratie- en bufferrijen
// Testfixtures zijn geen gebruikersworkflow.
using System.Text;
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TrainingEntity = AalstAcademie.Web.Models.Domain.Training;

namespace AalstAcademie.Tests.Data;

/// <summary>
/// Achttien echte SQLite-cases bewijzen compacte historie, UTC, actieve partiële
/// uniciteit en Restrict-FKs. De fixture levert geen aanvraag-, review- of promotieflow.
/// </summary>
public class TrainingFoundationPersistenceTests
{
    private static readonly DateTime NowUtc = new(2026, 10, 4, 12, 34, 56, DateTimeKind.Utc);

    // Aanwezige sluiting en null hebben aparte readbacks; UTC-kind en alle zeven fractiecijfers blijven behouden.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Utc_fields_and_nullable_closure_roundtrip(bool closed)
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var prepared = await PrepareAsync(db);
        var instant = NowUtc.AddTicks(1234567);
        var registration = RegistrationFor(prepared);
        registration.RequestedAtUtc = instant;
        var wait = WaitlistFor(prepared);
        wait.JoinedAtUtc = instant;
        // Closed>=Joined is geen gekozen opslagregel; dit bewijst dat geen extra tijdpolicy is ingevoerd.
        wait.ClosedAtUtc = closed ? instant.AddDays(-1) : null;
        db.Context.AddRange(registration, wait);
        await db.Context.SaveChangesAsync();
        db.Context.ChangeTracker.Clear();
        var storedRegistration = await db.Context.Registrations.AsNoTracking().SingleAsync();
        var storedWait = await db.Context.WaitlistEntries.AsNoTracking().SingleAsync();
        Assert.Equal(instant, storedRegistration.RequestedAtUtc);
        Assert.Equal(DateTimeKind.Utc, storedRegistration.RequestedAtUtc.Kind);
        Assert.Equal(instant, storedWait.JoinedAtUtc);
        Assert.Equal(DateTimeKind.Utc, storedWait.JoinedAtUtc.Kind);
        Assert.Equal(wait.ClosedAtUtc, storedWait.ClosedAtUtc);
        if (closed) Assert.Equal(DateTimeKind.Utc, storedWait.ClosedAtUtc!.Value.Kind);
        await using var command = db.Context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT RequestedAtUtc,typeof(RequestedAtUtc),(SELECT JoinedAtUtc FROM WaitlistEntries),(SELECT ClosedAtUtc FROM WaitlistEntries) FROM Registrations";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("2026-10-04T12:34:56.1234567Z", reader.GetString(0));
        Assert.Equal("text", reader.GetString(1));
        Assert.Equal(reader.GetString(0), reader.GetString(2));
        if (closed) Assert.Equal("2026-10-03T12:34:56.1234567Z", reader.GetString(3));
        else Assert.True(reader.IsDBNull(3));
    }

    // Requested en Confirmed delen hetzelfde actieve paar; Refused/Cancelled blijven als afzonderlijke pogingen.
    [Theory]
    [InlineData(RegistrationStatus.Requested)]
    [InlineData(RegistrationStatus.Confirmed)]
    public async Task Active_registration_pair_is_unique_while_history_remains(RegistrationStatus firstStatus)
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var prepared = await PrepareAsync(db);
        var first = RegistrationFor(prepared, firstStatus);
        db.Context.Registrations.Add(first);
        await db.Context.SaveChangesAsync();
        db.Context.Registrations.Add(RegistrationFor(prepared,
            firstStatus == RegistrationStatus.Requested ? RegistrationStatus.Confirmed : RegistrationStatus.Requested));
        await AssertUniqueFailureAsync(db);
        db.Context.ChangeTracker.Clear();
        db.Context.Registrations.AddRange(RegistrationFor(prepared, RegistrationStatus.Refused), RegistrationFor(prepared, RegistrationStatus.Cancelled));
        await db.Context.SaveChangesAsync();
        Assert.Equal(3, await db.Context.Registrations.AsNoTracking().CountAsync());
        Assert.Equal(firstStatus, await db.Context.Registrations.AsNoTracking().Where(x => x.Id == first.Id).Select(x => x.Status).SingleAsync());
    }

    // Alleen nullClosed is actief; eerdere gesloten posities worden niet overschreven of heropend.
    [Fact]
    public async Task Active_buffer_pair_is_unique_beside_closed_history()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var prepared = await PrepareAsync(db);
        var first = WaitlistFor(prepared);
        db.Context.WaitlistEntries.Add(first);
        await db.Context.SaveChangesAsync();
        db.Context.WaitlistEntries.Add(WaitlistFor(prepared));
        await AssertUniqueFailureAsync(db);
        db.Context.ChangeTracker.Clear();
        var closedOne = WaitlistFor(prepared); closedOne.ClosedAtUtc = NowUtc.AddHours(1);
        var closedTwo = WaitlistFor(prepared); closedTwo.ClosedAtUtc = NowUtc.AddHours(2);
        db.Context.WaitlistEntries.AddRange(closedOne, closedTwo);
        await db.Context.SaveChangesAsync();
        Assert.Equal(3, await db.Context.WaitlistEntries.AsNoTracking().CountAsync());
        Assert.Equal(first.Id, await db.Context.WaitlistEntries.AsNoTracking().Where(x => x.ClosedAtUtc == null).Select(x => x.Id).SingleAsync());
    }

    // Alle vier foundationverwijzingen zijn fysieke verplichte FKs, zonder extra TrainingId op Registration.
    [Theory]
    [InlineData("registration-moment")]
    [InlineData("registration-user")]
    [InlineData("waitlist-training")]
    [InlineData("waitlist-user")]
    public async Task Required_foundation_foreign_keys_reject_missing_parent(string relationship)
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var prepared = await PrepareAsync(db);
        var registration = RegistrationFor(prepared); var wait = WaitlistFor(prepared);
        db.Context.AddRange(registration, wait);
        await db.Context.SaveChangesAsync();
        var isRegistration = relationship.StartsWith("registration", StringComparison.Ordinal);
        var table = isRegistration ? "Registrations" : "WaitlistEntries";
        var column = relationship.EndsWith("user", StringComparison.Ordinal) ? "ApplicationUserId" : isRegistration ? "TrainingMomentId" : "TrainingId";
        var id = isRegistration ? registration.Id : wait.Id;
        object unknown = column == "ApplicationUserId" ? "missing-account" : 999999;
        var error = await RejectAsync(db, table, id, column, unknown);
        Assert.Equal(787, error.SqliteExtendedErrorCode);
        await RejectAsync(db, table, id, column, null);
        Assert.Single(await db.Context.Registrations.AsNoTracking().ToListAsync());
        Assert.Single(await db.Context.WaitlistEntries.AsNoTracking().ToListAsync());
    }

    // Iedere relatie wordt zonder andere blokkerende foundation/FK getest; gesloten/geweigerde historie blijft beschermd.
    [Theory]
    [InlineData("registration-moment")]
    [InlineData("registration-user")]
    [InlineData("waitlist-training")]
    [InlineData("waitlist-user")]
    public async Task Foundation_history_restricts_each_parent_delete(string relationship)
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var prepared = await PrepareAsync(db);
        if (relationship.StartsWith("registration", StringComparison.Ordinal))
        {
            var registration = RegistrationFor(prepared);
            db.Context.Registrations.Add(registration);
            await db.Context.SaveChangesAsync();
            foreach (var status in Enum.GetValues<RegistrationStatus>())
            {
                registration.Status = status;
                await db.Context.SaveChangesAsync();
                await RejectDeleteAsync(db, relationship == "registration-user" ? "AspNetUsers" : "TrainingMoments",
                    relationship == "registration-user" ? prepared.ParticipantId : prepared.Moment.Id);
                Assert.Single(await db.Context.Registrations.AsNoTracking().ToListAsync());
            }
        }
        else
        {
            // Verwijder alleen de referentievrije fixturemomentrij, zodat Waitlist zelf Trainingdelete bewijst.
            db.Context.TrainingMoments.Remove(prepared.Moment);
            await db.Context.SaveChangesAsync();
            var wait = WaitlistFor(prepared);
            db.Context.WaitlistEntries.Add(wait);
            await db.Context.SaveChangesAsync();
            foreach (var closed in new[] { false, true })
            {
                wait.ClosedAtUtc = closed ? NowUtc.AddHours(1) : null;
                await db.Context.SaveChangesAsync();
                await RejectDeleteAsync(db, relationship == "waitlist-user" ? "AspNetUsers" : "Trainings",
                    relationship == "waitlist-user" ? prepared.ParticipantId : prepared.Moment.TrainingId);
                Assert.Single(await db.Context.WaitlistEntries.AsNoTracking().ToListAsync());
            }
        }
    }

    // Raw scalars volgen type/limiet/vorm, ook bij een geldig Guid-/UTC-prefix vóór een verborgen NUL-suffix.
    // Lokale UTC-checks claimen geen volledige kalender- of workflowvalidatie.
    [Theory]
    [InlineData("Registrations")]
    [InlineData("WaitlistEntries")]
    public async Task Foundation_scalar_enum_text_and_utc_shapes_are_guarded(string table)
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var prepared = await PrepareAsync(db);
        var registration = RegistrationFor(prepared); var wait = WaitlistFor(prepared);
        db.Context.AddRange(registration, wait);
        await db.Context.SaveChangesAsync();
        var id = table == "Registrations" ? registration.Id : wait.Id;
        foreach (var motivation in new[] { new string('x', 4001), "reden\0suffix", "reden\0" + new string('x', 4001) })
            await RejectAsync(db, table, id, "Motivation", motivation);
        foreach (var value in new object?[] { null, "", "bad-guid", Guid.Empty.ToString(), Guid.NewGuid().ToString() + "\0suffix" })
            await RejectAsync(db, table, id, "Version", value);
        var utcColumn = table == "Registrations" ? "RequestedAtUtc" : "JoinedAtUtc";
        foreach (var value in new object?[] { null, "2026-10-04", "2026-10-04T12:34:56Z", "2026-10-04T12:34:56.1234567+02:00", "2026-10-04T12:34:56.1234567Z\0suffix", 123 })
            await RejectAsync(db, table, id, utcColumn, value);
        if (table == "Registrations")
        {
            foreach (var value in new object[] { -1, 4, 1.5, "invalid" }) await RejectAsync(db, table, id, "Status", value);
            foreach (var reason in new[] { new string('x', 1001), "reden\0suffix", "reden\0" + new string('x', 1001) })
                await RejectAsync(db, table, id, "DecisionReason", reason);
        }
        else foreach (var value in new object[] { "not-utc", "2026-10-04T12:34:56Z", "2026-10-04T12:34:56.1234567Z\0suffix", 123 })
            await RejectAsync(db, table, id, "ClosedAtUtc", value);
        Assert.Equal(NowUtc, await db.Context.Registrations.AsNoTracking().Select(x => x.RequestedAtUtc).SingleAsync());
        Assert.Null(await db.Context.WaitlistEntries.AsNoTracking().Select(x => x.ClosedAtUtc).SingleAsync());
    }

    // Een Local/Unspecified-waarde mag niet als Z-tekst worden vermomd, ook niet in de nullable sluitingskolom.
    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task Utc_converter_rejects_non_utc_writes_without_partial_changes(DateTimeKind kind)
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var prepared = await PrepareAsync(db);
        db.Context.AddRange(RegistrationFor(prepared), WaitlistFor(prepared));
        await db.Context.SaveChangesAsync();
        var invalid = DateTime.SpecifyKind(NowUtc, kind);
        Assert.Throws<ArgumentException>(() => TrainingValueConverters.UtcToText.ConvertToProvider(invalid));
        Assert.Throws<ArgumentException>(() => TrainingValueConverters.NullableUtcToText.ConvertToProvider((DateTime?)invalid));
        foreach (var field in new[] { "RequestedAtUtc", "JoinedAtUtc", "ClosedAtUtc" })
        {
            db.Context.ChangeTracker.Clear();
            if (field == "RequestedAtUtc")
            {
                var registration = await db.Context.Registrations.SingleAsync();
                registration.RequestedAtUtc = invalid;
                // DateTime.Equals vergelijkt ticks, niet Kind; deze proef moet werkelijk een write aanbieden.
                db.Context.Entry(registration).Property(x => x.RequestedAtUtc).IsModified = true;
            }
            else
            {
                var wait = await db.Context.WaitlistEntries.SingleAsync();
                if (field == "JoinedAtUtc")
                {
                    wait.JoinedAtUtc = invalid;
                    db.Context.Entry(wait).Property(x => x.JoinedAtUtc).IsModified = true;
                }
                else
                {
                    wait.ClosedAtUtc = invalid;
                    db.Context.Entry(wait).Property(x => x.ClosedAtUtc).IsModified = true;
                }
            }
            var error = await Record.ExceptionAsync(() => db.Context.SaveChangesAsync());
            Assert.NotNull(error);
            Assert.IsType<ArgumentException>(error.GetBaseException());
            Assert.Equal(NowUtc, await db.Context.Registrations.AsNoTracking().Select(x => x.RequestedAtUtc).SingleAsync());
            Assert.Equal(NowUtc, await db.Context.WaitlistEntries.AsNoTracking().Select(x => x.JoinedAtUtc).SingleAsync());
            Assert.Null(await db.Context.WaitlistEntries.AsNoTracking().Select(x => x.ClosedAtUtc).SingleAsync());
        }
    }

    // De fysieke tabelkolommen bewijzen de compacte opslag, zonder ongevraagde snapshots, bron of Waitliststatus.
    [Fact]
    public async Task Physical_foundation_schema_is_compact()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        Assert.Equal(new[] { "ApplicationUserId", "DecisionReason", "Id", "Motivation", "RequestedAtUtc", "Status", "TrainingMomentId", "Version" },
            await ColumnNamesAsync(db, "Registrations"));
        Assert.Equal(new[] { "ApplicationUserId", "ClosedAtUtc", "Id", "JoinedAtUtc", "Motivation", "TrainingId", "Version" },
            await ColumnNamesAsync(db, "WaitlistEntries"));
    }

    /// <summary>Afzonderlijke profielvrije deelnemer voorkomt dat een ongerelateerde profiel-FK de deleteproef verklaart.</summary>
    private static async Task<PreparedFoundation> PrepareAsync(SqliteTestDatabase db)
    {
        var owner = await db.AddUserAsync(RequestedAccountType.InternalInstructor);
        var participant = await db.AddUserAsync(RequestedAccountType.Employee);
        var category = new Category { Name = $"Historiecategorie {Guid.NewGuid():N}" };
        db.Context.Categories.Add(category);
        db.Context.Entry(category).Property<string>("NormalizedName").CurrentValue = category.Name.ToUpperInvariant();
        db.Context.Entry(category).Property<Guid>("Version").CurrentValue = Guid.NewGuid();
        var training = new TrainingEntity { Title = "Fictieve historieopleiding", Category = category, InstructorUserId = owner.Id };
        var location = new Location { Name = "Historiezaal", MaximumCapacity = 20 };
        var moment = new TrainingMoment { Training = training, Location = location, Date = new DateOnly(2027, 6, 15),
            StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 0), MaximumParticipants = 10 };
        db.Context.TrainingMoments.Add(moment);
        await db.Context.SaveChangesAsync();
        return new(moment, participant.Id);
    }

    private static Registration RegistrationFor(PreparedFoundation prepared, RegistrationStatus status = RegistrationStatus.Requested) =>
        new() { TrainingMomentId = prepared.Moment.Id, ApplicationUserId = prepared.ParticipantId, Status = status,
            Motivation = "Fictieve motivatie", RequestedAtUtc = NowUtc };
    private static WaitlistEntry WaitlistFor(PreparedFoundation prepared) => new()
        { TrainingId = prepared.Moment.TrainingId, ApplicationUserId = prepared.ParticipantId, Motivation = "Fictieve belangstelling", JoinedAtUtc = NowUtc };

    private static async Task AssertUniqueFailureAsync(SqliteTestDatabase db)
    {
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.Context.SaveChangesAsync());
        Assert.Equal(2067, Assert.IsType<SqliteException>(error.InnerException).SqliteExtendedErrorCode);
    }

    private static async Task<SqliteException> RejectAsync(SqliteTestDatabase db, string table, int id, string column, object? value)
    {
        var allowed = table == "Registrations" ? new[] { "TrainingMomentId", "ApplicationUserId", "Status", "Motivation", "RequestedAtUtc", "Version", "DecisionReason" }
            : table == "WaitlistEntries" ? new[] { "TrainingId", "ApplicationUserId", "Motivation", "JoinedAtUtc", "ClosedAtUtc", "Version" } : [];
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

    private static async Task RejectDeleteAsync(SqliteTestDatabase db, string table, object id)
    {
        if (table is not ("AspNetUsers" or "TrainingMoments" or "Trainings")) throw new ArgumentOutOfRangeException(nameof(table));
        // De tabelnaam is hierboven begrensd; de concrete sleutel wordt nooit in de SQL-tekst opgenomen.
        var sql = $"DELETE FROM {table} WHERE Id=@id";
        var error = await Assert.ThrowsAsync<SqliteException>(() => db.Context.Database.ExecuteSqlRawAsync(sql, new SqliteParameter("@id", id)));
        Assert.Equal(19, error.SqliteErrorCode);
    }

    private static async Task<string[]> ColumnNamesAsync(SqliteTestDatabase db, string table)
    {
        if (table is not ("Registrations" or "WaitlistEntries")) throw new ArgumentOutOfRangeException(nameof(table));
        await using var command = db.Context.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync()) names.Add(reader.GetString(1));
        return names.Order(StringComparer.Ordinal).ToArray();
    }
    private sealed record PreparedFoundation(TrainingMoment Moment, string ParticipantId);
}
