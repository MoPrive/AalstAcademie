using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using Microsoft.EntityFrameworkCore;

namespace AalstAcademie.Web.Data;

/// <summary>EF Fluent API: vertaalt de accountmodellen naar kolommen, relaties en constraints.</summary>
internal static class AccountFoundationConfiguration
{
    /// <summary>Configureert het model; voert zelf geen SQL of accountgoedkeuring uit.</summary>
    public static void Configure(ModelBuilder builder)
    {
        // Houd de oorspronkelijke sleutellengten ook bij tooling/testcontexts gelijk aan
        // AddDefaultIdentity. Zo ontstaat geen onbedoelde Identity-schemawijziging.
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserLogin<string>>(b =>
        {
            b.Property(x => x.LoginProvider).HasMaxLength(128);
            b.Property(x => x.ProviderKey).HasMaxLength(128);
        });
        builder.Entity<Microsoft.AspNetCore.Identity.IdentityUserToken<string>>(b =>
        {
            b.Property(x => x.LoginProvider).HasMaxLength(128);
            b.Property(x => x.Name).HasMaxLength(128);
        });
        // Nullable profielkolommen houden oude/incomplete accounts geldig bij de upgrade.
        // De expliciete enumconversie bewaart de vaste numerieke waarden in SQLite.
        builder.Entity<ApplicationUser>(b =>
        {
            b.Property(x => x.RequestedAccountType).HasConversion<int>();
            b.Property(x => x.AccountApprovalStatus).HasConversion<int>().HasDefaultValue(AccountApprovalStatus.Pending);
            b.Property(x => x.IsBlocked).HasDefaultValue(false);
            b.Property(x => x.FirstName).HasMaxLength(100);
            b.Property(x => x.LastName).HasMaxLength(100);
            // Sprint 004: geen naamkopie; beide accountrelaties bewaren hun doel bij latere rolwijzigingen.
            b.HasOne(x => x.Manager).WithMany().HasForeignKey(x => x.ManagerUserId).OnDelete(DeleteBehavior.Restrict);
            // De maximale redenlengte sluit aan op de servicevalidatie van een weigering.
            b.Property(x => x.RefusalReason).HasMaxLength(1000);
            b.Property(x => x.DemoSeedKey).HasMaxLength(64);
            // Meerdere legacy/null-markers zijn toegestaan; een concrete seedmarker is uniek.
            // Samen met gereserveerde ID/e-mail voorkomt dit dat bootstrap een ander account overneemt.
            b.HasIndex(x => x.DemoSeedKey).IsUnique();
            // Restrict voorkomt het verwijderen van een afdeling of beoordelaar waarnaar
            // een gebruiker verwijst; dit voorkomt stil verlies van die koppeling.
            b.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(x => x.ReviewedBy).WithMany().HasForeignKey(x => x.ReviewedById).OnDelete(DeleteBehavior.Restrict);
        });
        // SQLite handhaaft HasMaxLength niet als lengteconstraint. De CHECK voegt
        // daarom een echte opslagcontrole op de getrimde afdelingsnaam toe.
        builder.Entity<Department>(b =>
        {
            b.ToTable("Departments", t => t.HasCheckConstraint("CK_Department_Name", "length(trim(Name)) BETWEEN 1 AND 100"));
            b.Property(x => x.Name).IsRequired().HasMaxLength(100);
            b.HasIndex(x => x.Name).IsUnique();
            b.Property(x => x.ResponsibleUserId).IsConcurrencyToken();
            b.HasOne(x => x.ResponsibleUser).WithMany().HasForeignKey(x => x.ResponsibleUserId).OnDelete(DeleteBehavior.Restrict);
        });
        // Eén-op-één met het account en een unieke FK per interne-profieltabel.
        // Dit is nog geen exclusiviteit tussen de interne en externe tabellen.
        builder.Entity<InternalInstructor>(b =>
        {
            b.ToTable("InternalInstructors");
            b.Property(x => x.ApplicationUserId).IsRequired();
            b.HasIndex(x => x.ApplicationUserId).IsUnique();
            b.HasOne(x => x.User).WithOne().HasForeignKey<InternalInstructor>(x => x.ApplicationUserId).OnDelete(DeleteBehavior.Restrict);
        });
        // Het externe profiel heeft eigen organisatiegegevens, dezelfde unieke accountlink
        // en databasecontroles voor de verplichte naam en optionele btw-lengte.
        builder.Entity<ExternalInstructor>(b =>
        {
            b.ToTable("ExternalInstructors", t =>
            {
                t.HasCheckConstraint("CK_ExternalInstructor_Name", "length(trim(OrganizationName)) BETWEEN 1 AND 200");
                t.HasCheckConstraint("CK_ExternalInstructor_Vat", "VatNumber IS NULL OR length(VatNumber) <= 32");
            });
            b.Property(x => x.ApplicationUserId).IsRequired();
            b.HasIndex(x => x.ApplicationUserId).IsUnique();
            b.Property(x => x.OrganizationName).IsRequired().HasMaxLength(200);
            b.Property(x => x.VatNumber).HasMaxLength(32);
            b.HasOne(x => x.User).WithOne().HasForeignKey<ExternalInstructor>(x => x.ApplicationUserId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
