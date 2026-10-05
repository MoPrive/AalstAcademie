// Sprint 003: Controleert Belgische tijdzone, niet-bestaande en dubbelzinnige DST-tijden, strikte parsing, tickprecisie en de grens op de opgeslagen start of het opgeslagen einde.
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Services.Training;

namespace AalstAcademie.Tests.Training;

/// <summary>Eenentwintig pure cases toetsen Belgische tijd, DST, parsing en de strikte opgeslagen-startgrens.</summary>
public class TrainingScheduleTests
{
    // S1: timezone en tickprecisie komen uit het expliciete contract, niet uit de host-local klok.
    [Theory]
    [InlineData("winter", 1)]
    [InlineData("summer", 2)]
    [InlineData("subsecond", 2)]
    public void Belgian_schedule_roundtrips(string variant, int offsetHours)
    {
        var date = variant == "winter" ? new DateOnly(2027, 1, 15) : new DateOnly(2027, 6, 15);
        var start = new TimeOnly(9, 15);
        var end = new TimeOnly(10, 15);
        if (variant == "subsecond") { start = start.Add(TimeSpan.FromTicks(1234567)); end = end.Add(TimeSpan.FromTicks(7654321)); }
        var schedule = new TrainingSchedule(new FixedTimeProvider());
        var result = schedule.Validate(date, start, end);
        Assert.True(result.Succeeded);
        Assert.Equal(date, result.Value!.Date);
        Assert.Equal(start.Ticks, result.Value.StartTime.Ticks);
        Assert.Equal(end.Ticks, result.Value.EndTime.Ticks);
        Assert.Equal(new DateTimeOffset(date.ToDateTime(start), TimeSpan.Zero).AddHours(-offsetHours), result.Value.StartUtc);
        Assert.Equal(new DateTimeOffset(date.ToDateTime(end), TimeSpan.Zero).AddHours(-offsetHours), result.Value.EndUtc);
        Assert.Equal(TimeSpan.Zero, result.Value.StartUtc.Offset);
        // Een geldige verledenaanmaak is toegestaan; dit is geen editbaarheidstoets.
        Assert.True(schedule.Validate(new DateOnly(2026, 1, 15), new TimeOnly(9, 0), new TimeOnly(10, 0)).Succeeded);
    }

    // S2: beide eindpunten worden afzonderlijk gecontroleerd; een DST-overlap kiest niet stil één offset.
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void Dst_invalid_or_ambiguous_endpoint_is_rejected(bool ambiguous, bool badStart)
    {
        var date = ambiguous ? new DateOnly(2026, 10, 25) : new DateOnly(2026, 3, 29);
        var start = badStart ? new TimeOnly(2, 30) : new TimeOnly(1, 30);
        var end = badStart ? new TimeOnly(3, 30) : new TimeOnly(2, 30);
        var result = new TrainingSchedule(new FixedTimeProvider()).Validate(date, start, end);
        Assert.False(result.Succeeded);
        Assert.Contains(badStart ? "StartTime" : "EndTime", result.Errors.Keys);
        Assert.Null(result.Value);
    }

    // S3: elke rij is een samenhangend parsing-/volgordecontract met expliciete foutgrenzen.
    [Theory]
    [InlineData("date")]
    [InlineData("time")]
    [InlineData("order")]
    [InlineData("precision")]
    public void Schedule_input_has_no_fallback(string group)
    {
        if (group == "date")
        {
            foreach (var text in new string?[] { null, "", "abc", "2026-02-30", "03/10/2026", "10000-01-01" })
            { Assert.False(TrainingSchedule.TryParseDate(text, out var date)); Assert.Null(date); }
            Assert.True(TrainingSchedule.TryParseDate("2027-06-15", out var accepted));
            Assert.Equal(new DateOnly(2027, 6, 15), accepted);
        }
        else if (group == "time")
        {
            foreach (var text in new string?[] { null, "", "abc", "09:60", "24:00", "9:00" })
            { Assert.False(TrainingSchedule.TryParseTime(text, out var time)); Assert.Null(time); }
            Assert.True(TrainingSchedule.TryParseTime("09:15", out var accepted));
            Assert.Equal(new TimeOnly(9, 15), accepted);
        }
        else if (group == "order")
        {
            var schedule = new TrainingSchedule(new FixedTimeProvider());
            foreach (var end in new[] { new TimeOnly(9, 0), new TimeOnly(8, 59) })
            {
                var result = schedule.Validate(new DateOnly(2027, 6, 15), new TimeOnly(9, 0), end);
                Assert.False(result.Succeeded);
                Assert.Contains("EndTime", result.Errors.Keys);
            }
            Assert.False(schedule.Validate(null, new TimeOnly(9, 0), new TimeOnly(10, 0)).Succeeded);
            Assert.False(schedule.Validate(new DateOnly(2027, 6, 15), null, new TimeOnly(10, 0)).Succeeded);
            Assert.False(schedule.Validate(new DateOnly(2027, 6, 15), new TimeOnly(9, 0), null).Succeeded);
        }
        else
        {
            Assert.False(TrainingSchedule.TryParseTime("09:15:00.12345678", out var rejected));
            Assert.Null(rejected);
            Assert.True(TrainingSchedule.TryParseTime("09:15:00.1234567", out var precise));
            Assert.Equal(new TimeOnly(9, 15).Ticks + 1234567, precise!.Value.Ticks);
            Assert.True(TrainingSchedule.TryParseTime("09:15:01", out var seconds));
            Assert.Equal(new TimeOnly(9, 15, 1), seconds);
        }
    }

    // S4: beide toestanden en alle startgrenzen lezen uitsluitend de opgeslagen momentplanning.
    [Theory]
    [InlineData(false, -1)]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, -1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public void Mutation_requires_scheduled_and_strictly_before_stored_start(bool cancelled, int relativeTick)
    {
        var date = new DateOnly(2027, 6, 15);
        var start = new TimeOnly(9, 0).Add(TimeSpan.FromTicks(1234567));
        var utcStart = new DateTimeOffset(date.ToDateTime(start), TimeSpan.Zero).AddHours(-2);
        var clock = new FixedTimeProvider(utcStart.AddTicks(relativeTick));
        var schedule = new TrainingSchedule(clock);
        Assert.Equal(relativeTick < 0, schedule.IsBeforeStoredStart(date, start));
        var moment = new TrainingMoment { Date = date, StartTime = start, EndTime = new TimeOnly(10, 0),
            Status = cancelled ? TrainingMomentStatus.Cancelled : TrainingMomentStatus.Scheduled };
        Assert.Equal(!cancelled && relativeTick < 0, schedule.CanEdit(moment));
    }

    // S6: zaalverlaging beschermt ook een begonnen moment zolang het volledige opgeslagen einde nog volgt.
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void Stored_end_has_a_strict_full_precision_boundary(int relativeTick)
    {
        var date = new DateOnly(2027, 1, 15);
        var end = new TimeOnly(10, 0).Add(TimeSpan.FromTicks(7654321));
        var utcEnd = new DateTimeOffset(date.ToDateTime(end), TimeSpan.Zero).AddHours(-1);
        var clock = new FixedTimeProvider(utcEnd.AddTicks(relativeTick));
        var schedule = new TrainingSchedule(clock);
        Assert.Equal(relativeTick < 0, schedule.IsBeforeStoredEnd(date, end));
        Assert.False(schedule.IsBeforeStoredStart(date, new TimeOnly(9, 0)));
        clock.UtcNow = clock.UtcNow.AddDays(1);
        Assert.False(schedule.IsBeforeStoredEnd(date, end));
    }

    // S5: de ontbrekende zonedata zijn een configuratiefout, geen aanleiding voor een stil UTC-default.
    [Fact]
    public void Missing_timezone_aborts_configuration()
    {
        var ids = new List<string>();
        TimeZoneInfo Missing(string id) { ids.Add(id); throw new TimeZoneNotFoundException("Fictieve ontbrekende tijdzone."); }
        Assert.Throws<InvalidOperationException>(() => new TrainingSchedule(new FixedTimeProvider(), Missing));
        Assert.Equal(new[] { "Europe/Brussels", "Romance Standard Time" }, ids);
    }
}
