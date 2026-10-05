// Sprint 003: Onderscheidt alle registratie- en bufferhistorie van werkelijk bezette plaatsen
// Alleen Requested en Confirmed tellen mee voor bezetting van één moment.
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static AalstAcademie.Tests.Training.TrainingServiceTestSupport;

namespace AalstAcademie.Tests.Training;

/// <summary>Dertien echte opslagvarianten onderscheiden alle historie van alleen werkelijk bezette plaatsen.</summary>
public sealed class TrainingFoundationQueriesTests
{
    [Theory] [InlineData("none")] [InlineData("Requested")] [InlineData("Confirmed")] [InlineData("Refused")]
    [InlineData("Cancelled")] [InlineData("active-buffer")] [InlineData("closed-buffer")]
    public async Task Training_history_uses_all_statuses_and_closed_buffers_without_other_training_leakage(string variant)
    {
        await using var db=await Fixture();var owner=await TrainingTestData.CreateOwnerAsync(db.Services);
        var training=await TrainingTestData.CreateTrainingAsync(db.Services,owner);var other=await TrainingTestData.CreateTrainingAsync(db.Services,owner);
        if(variant!="none")await AddHistory(db,training,variant);var before=await Digest(db);
        await using var read=db.CreateScope();var queries=read.ServiceProvider.GetRequiredService<TrainingFoundationQueries>();
        Assert.Equal(variant!="none",await queries.HasTrainingHistoryAsync(training.Id));Assert.False(await queries.HasTrainingHistoryAsync(other.Id));
        Assert.False(await queries.HasTrainingHistoryAsync(int.MaxValue));Assert.Equal(before,await Digest(db));
    }

    [Theory] [InlineData(RegistrationStatus.Requested)] [InlineData(RegistrationStatus.Confirmed)]
    [InlineData(RegistrationStatus.Refused)] [InlineData(RegistrationStatus.Cancelled)]
    public async Task Every_registration_status_locks_only_its_own_moment(RegistrationStatus status)
    {
        await using var db=await Fixture();var training=await TrainingTestData.CreateTrainingAsync(db.Services,await TrainingTestData.CreateOwnerAsync(db.Services));
        var moment=await TrainingTestData.CreateMomentAsync(db.Services,training);var other=await TrainingTestData.CreateMomentAsync(db.Services,training,date:moment.Date.AddDays(1));
        await TrainingTestData.CreateRegistrationAsync(db.Services,moment,status);var before=await Digest(db);
        await using var read=db.CreateScope();var queries=read.ServiceProvider.GetRequiredService<TrainingFoundationQueries>();
        Assert.True(await queries.HasMomentRegistrationsAsync(moment.Id));Assert.False(await queries.HasMomentRegistrationsAsync(other.Id));
        Assert.False(await queries.HasMomentRegistrationsAsync(int.MaxValue));Assert.Equal(before,await Digest(db));
    }

    [Fact]
    public async Task Occupied_count_includes_only_requested_confirmed_of_the_selected_moment()
    {
        await using var db=await Fixture();var training=await TrainingTestData.CreateTrainingAsync(db.Services,await TrainingTestData.CreateOwnerAsync(db.Services));
        var moment=await TrainingTestData.CreateMomentAsync(db.Services,training);
        foreach(var status in Enum.GetValues<RegistrationStatus>())await TrainingTestData.CreateRegistrationAsync(db.Services,moment,status);
        var other=await TrainingTestData.CreateMomentAsync(db.Services,training,date:moment.Date.AddDays(1));await TrainingTestData.CreateRegistrationAsync(db.Services,other);
        await using var read=db.CreateScope();var query=read.ServiceProvider.GetRequiredService<TrainingFoundationQueries>();
        Assert.Equal(2,await query.CountOccupiedAsync(moment.Id));Assert.Equal(1,await query.CountOccupiedAsync(other.Id));Assert.Equal(0,await query.CountOccupiedAsync(int.MaxValue));
    }

    [Fact]
    public async Task Independent_queries_never_track_entities_or_change_any_stored_value()
    {
        await using var db=await Fixture();var training=await TrainingTestData.CreateTrainingAsync(db.Services,await TrainingTestData.CreateOwnerAsync(db.Services));
        var moment=await TrainingTestData.CreateMomentAsync(db.Services,training);await TrainingTestData.CreateRegistrationAsync(db.Services,moment);
        await TrainingTestData.CreateWaitlistEntryAsync(db.Services,training,true);var before=await Digest(db);
        await using var read=db.CreateScope();var query=read.ServiceProvider.GetRequiredService<TrainingFoundationQueries>();
        Assert.True(await query.HasTrainingHistoryAsync(training.Id));Assert.True(await query.HasMomentRegistrationsAsync(moment.Id));Assert.Equal(1,await query.CountOccupiedAsync(moment.Id));
        Assert.Empty(read.ServiceProvider.GetRequiredService<ApplicationDbContext>().ChangeTracker.Entries());Assert.Equal(before,await Digest(db));
    }
}
