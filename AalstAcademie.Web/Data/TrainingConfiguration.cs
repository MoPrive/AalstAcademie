// Sprint 003: Mapt categorie, definitie, doelgroepkoppeling, zaal, moment, registratie en buffer met constraints, restrictieve FKs, concurrencytokens en gedeeltelijke unieke indexen.
using AalstAcademie.Web.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace AalstAcademie.Web.Data;

/// <summary>
/// Fysieke opslagintegriteit voor de zeven opleidings-, zaal-, moment- en historietabellen.
/// Current-role, Belgische DST en aantallen doelgroepkoppelingen vereisen daarnaast services/transacties.
/// </summary>
internal static class TrainingConfiguration
{
    // SQLite bewaart Guid via tekst. Een D-vorm en niet-nulwaarde zijn lokale opslaginvarianten,
    // terwijl de applicatie bij elke mutatie zelf de volgende concurrencyversie schrijft.
    // SQLite length/GLOB kunnen bij NUL alleen het prefix beoordelen; instr controleert de volledige tekst.
    private const string GuidCheck = """
        typeof(Version) = 'text' AND length(Version) = 36 AND instr(Version,char(0)) = 0
        AND Version <> '00000000-0000-0000-0000-000000000000'
        AND substr(Version,9,1) = '-' AND substr(Version,14,1) = '-'
        AND substr(Version,19,1) = '-' AND substr(Version,24,1) = '-'
        AND length(replace(Version,'-','')) = 32
        AND replace(Version,'-','') NOT GLOB '*[^0-9A-Fa-f]*'
        """;

    /// <summary>Voegt modelregels toe; voert geen migratie, referentie-initialisatie of seed uit.</summary>
    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<Category>(b =>
        {
            b.ToTable("Categories", table =>
            {
                table.HasCheckConstraint("CK_Category_Name", "typeof(Name) = 'text' AND length(Name) <= 100 AND length(trim(Name)) >= 1 AND instr(Name,char(0)) = 0");
                table.HasCheckConstraint("CK_Category_NormalizedName", "typeof(NormalizedName) = 'text' AND length(NormalizedName) <= 100 AND length(trim(NormalizedName)) >= 1 AND instr(NormalizedName,char(0)) = 0");
                table.HasCheckConstraint("CK_Category_Version", GuidCheck);
            });
            b.Property(x => x.Name).IsRequired().HasMaxLength(100);
            // Alleen de service berekent FormC/whitespace/casenormalisatie; SQLite NOCASE
            // is niet onze Unicode-normalisatie. De BINARY-index bewaakt het resultaat exact.
            b.Property<string>("NormalizedName").IsRequired().HasMaxLength(100).UseCollation("BINARY");
            b.HasIndex("NormalizedName").IsUnique();
            b.Property<Guid>("Version").IsRequired().HasColumnType("TEXT").IsConcurrencyToken().ValueGeneratedNever();
        });

        builder.Entity<Training>(b =>
        {
            b.ToTable("Trainings", table =>
            {
                table.HasCheckConstraint("CK_Training_Title", "typeof(Title) = 'text' AND length(Title) <= 200 AND length(trim(Title)) >= 1 AND instr(Title,char(0)) = 0");
                table.HasCheckConstraint("CK_Training_Description", "Description IS NULL OR (typeof(Description) = 'text' AND length(Description) <= 4000 AND instr(Description,char(0)) = 0)");
                table.HasCheckConstraint("CK_Training_Instructor", "typeof(InstructorUserId) = 'text' AND length(trim(InstructorUserId)) >= 1 AND instr(InstructorUserId,char(0)) = 0");
                // De account-FK bewijst geen intern/extern profiel; die compatibiliteit blijft servervalidatie.
                table.HasCheckConstraint("CK_Training_Price", "ExternalTotalPriceCents IS NULL OR (typeof(ExternalTotalPriceCents) = 'integer' AND ExternalTotalPriceCents >= 0)");
                table.HasCheckConstraint("CK_Training_Audience", "typeof(AudienceScope) = 'integer' AND AudienceScope IN (0,1,2)");
                table.HasCheckConstraint("CK_Training_Booleans", "typeof(RequiresMotivation) = 'integer' AND RequiresMotivation IN (0,1)");
                table.HasCheckConstraint("CK_Training_Version", GuidCheck);
            });
            b.Property(x => x.Title).IsRequired().HasMaxLength(200);
            b.Property(x => x.Description).HasMaxLength(4000);
            b.Property(x => x.InstructorUserId).IsRequired();
            b.Property(x => x.ExternalTotalPriceEuros).HasColumnName("ExternalTotalPriceCents").HasColumnType("INTEGER")
                .HasConversion(TrainingValueConverters.ExactEuroCents);
            b.Property(x => x.AudienceScope).HasConversion<int>().HasDefaultValue(TrainingAudienceScope.Unassigned);
            b.Property(x => x.Version).HasColumnType("TEXT").IsConcurrencyToken().ValueGeneratedNever();
            b.HasOne(x => x.Category).WithMany(x => x.Trainings).HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(x => x.Instructor).WithMany().HasForeignKey(x => x.InstructorUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.InstructorUserId);
            b.HasIndex(x => x.CategoryId);
        });

        builder.Entity<TrainingDepartment>(b =>
        {
            b.ToTable("TrainingDepartments");
            b.HasKey(x => new { x.TrainingId, x.DepartmentId });
            b.HasIndex(x => x.DepartmentId);
            // Cascade is alleen referentiële cleanup. Er bestaat geen applicatie-delete voor een opleiding.
            b.HasOne(x => x.Training).WithMany(x => x.TrainingDepartments).HasForeignKey(x => x.TrainingId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(x => x.Department).WithMany(x => x.TrainingDepartments).HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            // Selected-minimum en nul links bij Unassigned/All zijn cross-table-regels,
            // niet met een lokale Training-CHECK te bewijzen. De schrijfservice bewaakt die atomisch.
        });

        builder.Entity<Location>(b =>
        {
            b.ToTable("Locations", table =>
            {
                table.HasCheckConstraint("CK_Location_Name", "typeof(Name) = 'text' AND length(Name) <= 200 AND length(trim(Name)) >= 1 AND instr(Name,char(0)) = 0");
                table.HasCheckConstraint("CK_Location_Address", "Address IS NULL OR (typeof(Address) = 'text' AND length(Address) <= 500 AND instr(Address,char(0)) = 0)");
                table.HasCheckConstraint("CK_Location_Capacity", "typeof(MaximumCapacity) = 'integer' AND MaximumCapacity > 0");
                table.HasCheckConstraint("CK_Location_Version", GuidCheck);
            });
            b.Property(x => x.Name).IsRequired().HasMaxLength(200);
            b.Property(x => x.Address).HasMaxLength(500);
            b.Property(x => x.Version).HasColumnType("TEXT").IsConcurrencyToken().ValueGeneratedNever();
            // Geen unieke zaalnaam of beschikbaarheidsflag: dat is geen gekozen productregel.
        });

        builder.Entity<TrainingMoment>(b =>
        {
            b.ToTable("TrainingMoments", table =>
            {
                // Het formaat alleen volstaat niet: een verborgen NUL-suffix mag geen geldige datum lijken.
                table.HasCheckConstraint("CK_TrainingMoment_Date", "typeof(Date) = 'text' AND length(Date) = 10 AND instr(Date,char(0)) = 0 AND Date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'");
                table.HasCheckConstraint("CK_TrainingMoment_Time", "typeof(StartTimeTicks) = 'integer' AND typeof(EndTimeTicks) = 'integer' AND StartTimeTicks BETWEEN 0 AND 863999999999 AND EndTimeTicks BETWEEN 0 AND 863999999999 AND EndTimeTicks > StartTimeTicks");
                table.HasCheckConstraint("CK_TrainingMoment_Capacity", "typeof(MaximumParticipants) = 'integer' AND MaximumParticipants > 0");
                table.HasCheckConstraint("CK_TrainingMoment_Status", "typeof(Status) = 'integer' AND Status IN (1,2)");
                table.HasCheckConstraint("CK_TrainingMoment_Version", GuidCheck);
            });
            b.Property(x => x.Date).HasColumnType("TEXT").HasConversion(TrainingValueConverters.DateToText);
            b.Property(x => x.StartTime).HasColumnName("StartTimeTicks").HasColumnType("INTEGER").HasConversion(TrainingValueConverters.TimeToTicks);
            b.Property(x => x.EndTime).HasColumnName("EndTimeTicks").HasColumnType("INTEGER").HasConversion(TrainingValueConverters.TimeToTicks);
            b.Property(x => x.Status).HasConversion<int>().HasDefaultValue(TrainingMomentStatus.Scheduled);
            b.Property(x => x.Version).HasColumnType("TEXT").IsConcurrencyToken().ValueGeneratedNever();
            b.HasOne(x => x.Training).WithMany(x => x.Moments).HasForeignKey(x => x.TrainingId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(x => x.Location).WithMany(x => x.Moments).HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.LocationId, x.Date, x.Status, x.StartTime });
            b.HasIndex(x => x.TrainingId);
            // DST, overlap en actuele zaalcap vereisen gezamenlijke servicevalidatie onder write-lock.
        });

        builder.Entity<Registration>(b =>
        {
            b.ToTable("Registrations", table =>
            {
                table.HasCheckConstraint("CK_Registration_Status", "typeof(Status) = 'integer' AND Status IN (0,1,2,3)");
                table.HasCheckConstraint("CK_Registration_Motivation", "Motivation IS NULL OR (typeof(Motivation) = 'text' AND length(Motivation) <= 4000 AND instr(Motivation,char(0)) = 0)");
                table.HasCheckConstraint("CK_Registration_DecisionReason", "DecisionReason IS NULL OR (typeof(DecisionReason) = 'text' AND length(DecisionReason) <= 1000 AND instr(DecisionReason,char(0)) = 0)");
                table.HasCheckConstraint("CK_Registration_RequestedAtUtc", UtcCheck("RequestedAtUtc"));
                table.HasCheckConstraint("CK_Registration_Version", GuidCheck);
            });
            b.Property(x => x.ApplicationUserId).IsRequired();
            b.Property(x => x.Status).HasConversion<int>();
            b.Property(x => x.Motivation).HasMaxLength(4000);
            b.Property(x => x.DecisionReason).HasMaxLength(1000);
            b.Property(x => x.RequestedAtUtc).HasColumnType("TEXT").HasConversion(TrainingValueConverters.UtcToText);
            b.Property(x => x.Version).HasColumnType("TEXT").IsConcurrencyToken().ValueGeneratedNever();
            b.HasOne(x => x.TrainingMoment).WithMany(x => x.Registrations).HasForeignKey(x => x.TrainingMomentId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(x => x.User).WithMany().HasForeignKey(x => x.ApplicationUserId).OnDelete(DeleteBehavior.Restrict);
            // Alleen actieve pogingen delen een uniek moment/account-paar; historie blijft opnieuw opslaanbaar.
            b.HasIndex(x => new { x.TrainingMomentId, x.ApplicationUserId }).IsUnique().HasFilter("Status IN (0,1)");
            b.HasIndex(x => new { x.TrainingMomentId, x.Status });
        });

        builder.Entity<WaitlistEntry>(b =>
        {
            b.ToTable("WaitlistEntries", table =>
            {
                table.HasCheckConstraint("CK_WaitlistEntry_Motivation", "Motivation IS NULL OR (typeof(Motivation) = 'text' AND length(Motivation) <= 4000 AND instr(Motivation,char(0)) = 0)");
                table.HasCheckConstraint("CK_WaitlistEntry_JoinedAtUtc", UtcCheck("JoinedAtUtc"));
                table.HasCheckConstraint("CK_WaitlistEntry_ClosedAtUtc", "ClosedAtUtc IS NULL OR (" + UtcCheck("ClosedAtUtc") + ")");
                table.HasCheckConstraint("CK_WaitlistEntry_Version", GuidCheck);
            });
            b.Property(x => x.ApplicationUserId).IsRequired();
            b.Property(x => x.Motivation).HasMaxLength(4000);
            b.Property(x => x.JoinedAtUtc).HasColumnType("TEXT").HasConversion(TrainingValueConverters.UtcToText);
            b.Property(x => x.ClosedAtUtc).HasColumnType("TEXT").HasConversion(TrainingValueConverters.NullableUtcToText);
            b.Property(x => x.Version).HasColumnType("TEXT").IsConcurrencyToken().ValueGeneratedNever();
            b.HasOne(x => x.Training).WithMany(x => x.WaitlistEntries).HasForeignKey(x => x.TrainingId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(x => x.User).WithMany().HasForeignKey(x => x.ApplicationUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.TrainingId, x.ApplicationUserId }).IsUnique().HasFilter("ClosedAtUtc IS NULL");
            b.HasIndex(x => new { x.TrainingId, x.JoinedAtUtc, x.Id });
            // Geen Status of Closed>=Joined-policy: null betekent actief, overige historie blijft behouden.
        });
    }
    /// <summary>Lokale vorm/typecheck zonder NUL-prefixtruc; echte kalender en UTC-kind bewaakt de converter.</summary>
    private static string UtcCheck(string column) => $"typeof({column}) = 'text' AND length({column}) = 28 AND instr({column},char(0)) = 0 AND {column} GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]Z'";
}
