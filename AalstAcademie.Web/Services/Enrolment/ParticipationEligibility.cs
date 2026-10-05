using AalstAcademie.Web.Configuration;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Enrolment;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;
using TrainingEntity = AalstAcademie.Web.Models.Domain.Training;

namespace AalstAcademie.Web.Services.Enrolment;

/// <summary>Een begrensde verzameling actuele rijen voor dezelfde opleidingen; geen nieuwe tabel of opgeslagen capaciteit.</summary>
public sealed record ParticipationSnapshot(IReadOnlyDictionary<int, TrainingEntity> Trainings,
    IReadOnlyList<TrainingMoment> Moments, IReadOnlyList<Registration> Registrations,
    IReadOnlyList<WaitlistEntry> Waitlist, IReadOnlyDictionary<string, int> EligibleDepartments);
public sealed record ParticipationAvailability(int PhysicalAvailable, int EligibleWaiters, int DirectAvailable);

/// <summary>Gedeelde D80-, tijd-, doelgroep- en wachtlijstregels voor leesmodellen en schrijftransacties.</summary>
public sealed class ParticipationEligibility(ApplicationDbContext context, TrainingSchedule schedule, DemoMode demoMode)
{
    public static MotivationValidation ValidateMotivation(string? raw, bool required)
    {
        var value = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        var errors = new Dictionary<string, string[]>();
        if (required && value is null) errors["Motivation"] = ["Vul een motivatie in."];
        else if (value is not null && (value.Length > 4000 || value.Contains('\0')))
            errors["Motivation"] = ["Gebruik maximaal 4000 tekens zonder nultekens."];
        return new(value, errors);
    }

    public static IReadOnlyDictionary<string, string[]> ValidateVersions(EmployeeRequestInput input)
    {
        var errors = new Dictionary<string, string[]>();
        if (input.ExpectedTrainingVersion is null || input.ExpectedTrainingVersion == Guid.Empty)
            errors["ExpectedTrainingVersion"] = ["De oorspronkelijke opleidingsversie ontbreekt."];
        if (input.ExpectedMomentVersion is null || input.ExpectedMomentVersion == Guid.Empty)
            errors["ExpectedMomentVersion"] = ["De oorspronkelijke momentversie ontbreekt."];
        return errors;
    }

    public static IReadOnlyDictionary<string, string[]> ValidateWaitlistInput(EmployeeWaitlistInput input)
    {
        var errors = new Dictionary<string, string[]>(ValidateVersions(input));
        if (!input.Consent) errors["Consent"] = ["Bevestig je belangstelling voor de wachtlijst."];
        return errors;
    }

    /// <summary>Enkele bulkqueries voorkomen een query per moment of wachtende. De caller bewaakt de transactie.</summary>
    public async Task<ParticipationSnapshot> ReadAsync(IEnumerable<int> trainingIds, CancellationToken ct = default)
    {
        var ids = trainingIds.Distinct().ToArray();
        var trainings = await context.Trainings.AsNoTracking().Include(x => x.TrainingDepartments)
            .Include(x => x.Category).Include(x => x.Instructor).Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        var moments = await context.TrainingMoments.AsNoTracking().Include(x => x.Location)
            .Where(x => ids.Contains(x.TrainingId)).ToListAsync(ct);
        var momentIds = moments.Select(x => x.Id).ToArray();
        var registrations = await context.Registrations.AsNoTracking().Where(x => momentIds.Contains(x.TrainingMomentId)).ToListAsync(ct);
        var waitlist = await context.WaitlistEntries.AsNoTracking().Where(x => ids.Contains(x.TrainingId))
            .OrderBy(x => x.JoinedAtUtc).ThenBy(x => x.Id).ToListAsync(ct);
        var waiterIds = waitlist.Where(x => x.ClosedAtUtc is null).Select(x => x.ApplicationUserId).Distinct().ToArray();
        var eligible = await (from user in context.Users.AsNoTracking()
            join department in context.Departments.AsNoTracking() on user.DepartmentId equals department.Id
            where waiterIds.Contains(user.Id) && !user.IsBlocked && user.AccountApprovalStatus == AccountApprovalStatus.Approved &&
                (demoMode.IsEnabled || user.DemoSeedKey == null) &&
                context.UserRoles.Any(grant => grant.UserId == user.Id && context.Roles.Any(role => role.Id == grant.RoleId &&
                    role.NormalizedName == RoleNames.Medewerker.ToUpperInvariant()))
            select new { user.Id, DepartmentId = department.Id }).ToDictionaryAsync(x => x.Id, x => x.DepartmentId, ct);
        return new(trainings.ToDictionary(x => x.Id), moments, registrations, waitlist, eligible);
    }

    public bool Allows(int departmentId, TrainingEntity training) => training.AudienceScope == TrainingAudienceScope.AllDepartments ||
        training.AudienceScope == TrainingAudienceScope.SelectedDepartments && training.TrainingDepartments.Any(x => x.DepartmentId == departmentId);
    public bool IsNewMoment(TrainingMoment moment) => schedule.CanEdit(moment);

    /// <summary>Requested/Confirmed blijven actief tot het opgeslagen einde. Alleen bij E telt de eigen concrete buffer niet mee.</summary>
    public bool HasActiveParticipation(string userId, int trainingId, ParticipationSnapshot state, int? ignoredBufferId = null) =>
        state.Waitlist.Any(x => x.TrainingId == trainingId && x.ApplicationUserId == userId && x.ClosedAtUtc is null && x.Id != ignoredBufferId) ||
        state.Registrations.Any(x => x.ApplicationUserId == userId && x.Status is RegistrationStatus.Requested or RegistrationStatus.Confirmed &&
            state.Moments.Any(m => m.Id == x.TrainingMomentId && m.TrainingId == trainingId && m.Status == TrainingMomentStatus.Scheduled &&
                schedule.IsBeforeStoredEnd(m.Date, m.EndTime)));
    public bool WasRefused(string userId, int momentId, ParticipationSnapshot state) =>
        state.Registrations.Any(x => x.ApplicationUserId == userId && x.TrainingMomentId == momentId && x.Status == RegistrationStatus.Refused);

    /// <summary>F is fysieke capaciteit; E reserveert conservatief voor nu geschikte wachtenden per gekozen moment.</summary>
    public ParticipationAvailability Availability(TrainingMoment moment, ParticipationSnapshot state)
    {
        var physical = Math.Max(0, moment.MaximumParticipants - state.Registrations.Count(x => x.TrainingMomentId == moment.Id &&
            x.Status is RegistrationStatus.Requested or RegistrationStatus.Confirmed));
        var training = state.Trainings[moment.TrainingId];
        var eligible = IsNewMoment(moment) ? state.Waitlist.Where(x => x.TrainingId == training.Id && x.ClosedAtUtc is null &&
            state.EligibleDepartments.TryGetValue(x.ApplicationUserId, out var departmentId) && Allows(departmentId, training) &&
            !HasActiveParticipation(x.ApplicationUserId, training.Id, state, x.Id) && !WasRefused(x.ApplicationUserId, moment.Id, state) &&
            ValidateMotivation(x.Motivation, training.RequiresMotivation).Succeeded).Select(x => x.ApplicationUserId).Distinct().Count() : 0;
        return new(physical, eligible, Math.Max(0, physical - eligible));
    }
}
