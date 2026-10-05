// Sprint 003: Schrijft drie categorieën, drie zalen, vier definities en vijf Scheduled-momenten als één aanbodbatch
// Botsende gereserveerde IDs worden geweigerd; registraties en buffer blijven leeg.
using AalstAcademie.Web.Configuration;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Services.Training;
using Microsoft.EntityFrameworkCore;
using TrainingEntity = AalstAcademie.Web.Models.Domain.Training;

namespace AalstAcademie.Web.Data;

/// <summary>De volledige afgesproken aanbodbatch wordt eenmaal opgebouwd vóór HTTP; geen herstel/marker of toekomstige inschrijfseed.</summary>
public sealed class DemoOfferSeeder(ApplicationDbContext context, DemoMode mode, TimeProvider clock, TrainingSchedule schedule)
{
    // Aanbod is een aparte batch na accounts; rollback van aanbod draait de al gecommitte accountbatch niet terug, maar startup blijft geweigerd.
    public async Task SeedAsync(CancellationToken token = default)
    {
        if (!mode.IsEnabled) return;
        if (context.Database.CurrentTransaction is not null) throw new InvalidOperationException("Aanbodseed vereist een eigen batchtransactie.");
        await using var transaction = await context.Database.BeginTransactionAsync(token);
        try
        {
            // Reserveringen worden niet geadopteerd, ook niet wanneer een vorige batch inhoudelijk gelijk lijkt.
            if (await context.Categories.AnyAsync(x => x.Id >= 300101 && x.Id <= 300103, token) ||
                await context.Locations.AnyAsync(x => x.Id >= 300201 && x.Id <= 300203, token) ||
                await context.Trainings.AnyAsync(x => x.Id >= 300301 && x.Id <= 300304, token) ||
                await context.TrainingMoments.AnyAsync(x => x.Id >= 300401 && x.Id <= 300405, token))
                throw new InvalidOperationException("Een gereserveerd aanbod-ID is bezet.");
            var access = new TrainingAccessReader(context, mode);
            var internalOwner = await access.ReadEligibleOwnerAsync(DemoAccountCatalog.InternalInstructorId, token);
            var externalOwner = await access.ReadEligibleOwnerAsync(DemoAccountCatalog.ExternalInstructorId, token);
            if (internalOwner?.IsExternal != false || externalOwner?.IsExternal != true)
                throw new InvalidOperationException("De complete geschikte demo-lesgevers moeten vóór aanbodseed aanwezig zijn.");
            // Gebruik de bestaande vier-afdelingenseed. De geselecteerde ICT-doelgroep is een echte koppeling, geen nieuwe afdelingsseed.
            var ict = await context.Departments.AsNoTracking().SingleAsync(x => x.Name == "ICT", token);
            var validation = new TrainingValueValidation(schedule);
            string[] names = ["Digitale vaardigheden", "Samenwerken", "Veilig werken"];
            for (var i = 0; i < names.Length; i++)
            {
                var valid = TrainingValueValidation.ValidateCategoryName(names[i]);
                if (!valid.Succeeded || await context.Categories.AnyAsync(x => EF.Property<string>(x, "NormalizedName") == valid.NormalizedName, token))
                    throw new InvalidOperationException("Een gereserveerde categorienaam is bezet of ongeldig.");
                var category = new Category { Id = 300101 + i, Name = valid.Name! };
                context.Categories.Add(category);
                context.Entry(category).Property<string>("NormalizedName").CurrentValue = valid.NormalizedName!;
                context.Entry(category).Property<Guid>("Version").CurrentValue = Guid.NewGuid();
            }
            Location[] rooms = [
                new() { Id = 300201, Name = "Demozaal A", Address = "Fictief demo-adres", MaximumCapacity = 24, Version = Guid.NewGuid() },
                new() { Id = 300202, Name = "Demozaal B", MaximumCapacity = 12, Version = Guid.NewGuid() },
                new() { Id = 300203, Name = "Vrije demozaal", MaximumCapacity = 8, Version = Guid.NewGuid() }];
            foreach (var room in rooms)
            {
                if (!validation.ValidateLocationFields(new(room.Name, room.Address, room.MaximumCapacity)).Succeeded)
                    throw new InvalidOperationException("Ongeldige zaal in aanbodbatch.");
                context.Locations.Add(room);
            }
            TrainingEntity[] trainings = [
                Definition(300301,"Excel basis",300101,internalOwner.UserId,TrainingAudienceScope.AllDepartments,false),
                Definition(300302,"Communicatie extern",300102,externalOwner.UserId,TrainingAudienceScope.SelectedDepartments,true,450.00m),
                Definition(300303,"Nieuwe interne workshop",300103,internalOwner.UserId,TrainingAudienceScope.Unassigned,true),
                Definition(300304,"Historische interne sessie",300101,internalOwner.UserId,TrainingAudienceScope.AllDepartments,false)];
            foreach (var training in trainings)
            {
                if (!validation.ValidateFields(new(training.Title,training.Description,training.CategoryId,training.ExternalTotalPriceEuros,
                    training.RequiresMotivation), training.InstructorUserId == externalOwner.UserId).Succeeded)
                    throw new InvalidOperationException("Ongeldige definitie in aanbodbatch.");
                context.Trainings.Add(training);
            }
            context.TrainingDepartments.Add(new TrainingDepartment { TrainingId = 300302, DepartmentId = ict.Id });
            // Kalender is expliciet Belgisch en wordt alleen bij een nieuwe volledige opbouw berekend.
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), schedule.TimeZone).DateTime);
            TrainingMoment[] moments = [
                Moment(300401,300301,300201,today.AddDays(7),9,11,16),
                Moment(300402,300301,300201,today.AddDays(7),11,13,16),
                Moment(300403,300302,300202,today.AddDays(8),10,12,10),
                Moment(300404,300303,300201,today.AddDays(9),9,10,12),
                Moment(300405,300304,300201,today.AddDays(-7),9,10,12)];
            foreach (var moment in moments)
            {
                if (!validation.ValidateMomentFields(new(moment.Date,moment.StartTime,moment.EndTime,moment.LocationId,moment.MaximumParticipants)).Succeeded ||
                    moment.MaximumParticipants > rooms.Single(x => x.Id == moment.LocationId).MaximumCapacity ||
                    moments.Any(x => x.Id != moment.Id && x.LocationId == moment.LocationId && x.Date == moment.Date &&
                        moment.StartTime < x.EndTime && x.StartTime < moment.EndTime) ||
                    await context.TrainingMoments.AnyAsync(x => x.LocationId == moment.LocationId && x.Date == moment.Date &&
                        x.Status == TrainingMomentStatus.Scheduled && moment.StartTime < x.EndTime && x.StartTime < moment.EndTime, token))
                    throw new InvalidOperationException("Ongeldige of overlappende planning in aanbodbatch.");
                context.TrainingMoments.Add(moment);
            }
            // Eén SaveChanges en commit: een fout na echte SQL draait de volledige aanbodbatch terug, niet de eerdere accountbatch.
            await context.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
        catch { await transaction.RollbackAsync(CancellationToken.None); context.ChangeTracker.Clear(); throw; }
    }
    // Bouw één gedeelde definitie; iedere uitvoering leest dezelfde actuele eigenaar en voorwaarden.
    private static TrainingEntity Definition(int id,string title,int category,string owner,TrainingAudienceScope scope,bool motivation,decimal? price=null) =>
        new() { Id=id,Title=title,Description="Fictieve demo-opleiding.",CategoryId=category,InstructorUserId=owner,AudienceScope=scope,
            RequiresMotivation=motivation,ExternalTotalPriceEuros=price,Version=Guid.NewGuid() };
    // Planning en deelnemersmaximum horen bij de uitvoering en krijgen een afzonderlijke versie.
    private static TrainingMoment Moment(int id,int training,int room,DateOnly date,int start,int end,int maximum) =>
        new() { Id=id,TrainingId=training,LocationId=room,Date=date,StartTime=new TimeOnly(start,0),EndTime=new TimeOnly(end,0),
            MaximumParticipants=maximum,Status=TrainingMomentStatus.Scheduled,Version=Guid.NewGuid() };
}

