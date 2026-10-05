// Sprint 003: Leest actuele accountstatus, rollen en profielvorm en gebruikt ApplicationUserId als enige eigenaar
// Nieuwe aanmaak eist een nu geschikte owner; bestaand adminbeheer blijft na ownerrolverlies mogelijk.
using AalstAcademie.Web.Configuration;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Models.Training;
using Microsoft.EntityFrameworkCore;
using TrainingEntity = AalstAcademie.Web.Models.Domain.Training;

namespace AalstAcademie.Web.Services.Training;

/// <summary>Leest actuele accountrechten; cookieclaims, requestcache en eerder verleende rollen zijn geen bewijs.</summary>
public sealed class TrainingAccessReader(ApplicationDbContext context, DemoMode demoMode)
{
    /// <summary>Ook directe servicecalls moeten de actuele goedkeuring, blokkering, demogrens en rollen passeren.</summary>
    public async Task<TrainingActor?> ReadActorAsync(string? userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return null;
        var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, cancellationToken);
        if (user is null || user.IsBlocked || user.AccountApprovalStatus != AccountApprovalStatus.Approved ||
            (!demoMode.IsEnabled && user.DemoSeedKey is not null)) return null;
        var roles = await (from grant in context.UserRoles.AsNoTracking()
            join role in context.Roles.AsNoTracking() on grant.RoleId equals role.Id
            where grant.UserId == userId select role.NormalizedName).ToListAsync(cancellationToken);
        var administrator = roles.Contains(RoleNames.Beheerder.ToUpperInvariant());
        var instructor = roles.Contains(RoleNames.Lesgever.ToUpperInvariant());
        return administrator || instructor ? new(user.Id, administrator, instructor) : null;
    }

    /// <summary>Nieuwe opleidingen en momenten eisen ook nu een geschikte vaste lesgever; dit repareert niets.</summary>
    public async Task<TrainingOwnerReadModel?> ReadEligibleOwnerAsync(string? userId, CancellationToken cancellationToken = default)
    {
        if ((await ReadActorAsync(userId, cancellationToken))?.IsInstructor != true) return null;
        return await ReadOwnerShapeAsync(userId, cancellationToken);
    }

    /// <summary>Bestaand beheer controleert prijsvorm/profiel, zonder eigenaar opnieuw Approved/unblocked/Lesgever te eisen.</summary>
    public async Task<TrainingOwnerReadModel?> ReadOwnerShapeAsync(string? userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return null;
        var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, cancellationToken);
        if (user is null) return null;
        var internals = await context.InternalInstructors.AsNoTracking().Where(x => x.ApplicationUserId == userId).ToListAsync(cancellationToken);
        var externals = await context.ExternalInstructors.AsNoTracking().Where(x => x.ApplicationUserId == userId).ToListAsync(cancellationToken);
        if (user.RequestedAccountType == RequestedAccountType.InternalInstructor && internals.Count == 1 && externals.Count == 0 &&
            !string.IsNullOrWhiteSpace(user.FirstName) && !string.IsNullOrWhiteSpace(user.LastName) &&
            user.FirstName.Length <= 100 && user.LastName.Length <= 100 && user.DepartmentId is not null &&
            await context.Departments.AsNoTracking().AnyAsync(x => x.Id == user.DepartmentId, cancellationToken))
            // Een ontbrekende optionele ManagerUserId is geen afwijzingsgrond voor een vaste eigenaar.
            return new(user.Id, false, internals[0].Id, $"{user.FirstName.Trim()} {user.LastName.Trim()}");
        if (user.RequestedAccountType == RequestedAccountType.ExternalInstructor && externals.Count == 1 && internals.Count == 0 &&
            !string.IsNullOrWhiteSpace(externals[0].OrganizationName) && externals[0].OrganizationName.Length <= 200)
            return new(user.Id, true, externals[0].Id, externals[0].OrganizationName.Trim());
        return null;
    }

    /// <summary>De opgeslagen ApplicationUserId bepaalt eigendom; profiel-ID's worden geen tweede eigenaarsbron.</summary>
    public Task<bool> CanReadTrainingAsync(TrainingActor actor, TrainingEntity training, CancellationToken cancellationToken = default) =>
        Task.FromResult(actor.IsAdministrator || training.InstructorUserId == actor.UserId);
}

/// <summary>Dubbele beheerrol krijgt adminscope; de actor blijft een beperkte actuele rechtenlezing.</summary>
public sealed record TrainingActor(string UserId, bool IsAdministrator, bool IsInstructor);
