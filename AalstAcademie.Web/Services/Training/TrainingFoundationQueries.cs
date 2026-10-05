// Sprint 003: Leest echte registratie- en bufferhistorie voor voorwaarden en planningguards
// Requested+Confirmed tellen plaatsen; Refused/Cancelled en gesloten buffers tellen nog steeds als historie.
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace AalstAcademie.Web.Services.Training;

/// <summary>Echte opslagqueries voor beheerguards; de aanroepende service controleert eerst resource en actor.</summary>
public sealed class TrainingFoundationQueries(ApplicationDbContext context)
{
    /// <summary>Ook geweigerde/geannuleerde aanvragen en gesloten buffers maken voorwaarden historisch.</summary>
    public async Task<bool> HasTrainingHistoryAsync(int trainingId, CancellationToken cancellationToken = default) =>
        await context.Registrations.AsNoTracking().AnyAsync(x => x.TrainingMoment.TrainingId == trainingId, cancellationToken) ||
        await context.WaitlistEntries.AsNoTracking().AnyAsync(x => x.TrainingId == trainingId, cancellationToken);

    /// <summary>Iedere bewaarde registratiestatus telt voor de planninglock van uitsluitend dit moment.</summary>
    public Task<bool> HasMomentRegistrationsAsync(int momentId, CancellationToken cancellationToken = default) =>
        context.Registrations.AsNoTracking().AnyAsync(x => x.TrainingMomentId == momentId, cancellationToken);

    /// <summary>Alleen Requested en Confirmed bezetten plaatsen; geen globale teller of promotiehook.</summary>
    public Task<int> CountOccupiedAsync(int momentId, CancellationToken cancellationToken = default) =>
        context.Registrations.AsNoTracking().CountAsync(x => x.TrainingMomentId == momentId &&
            (x.Status == RegistrationStatus.Requested || x.Status == RegistrationStatus.Confirmed), cancellationToken);
}
