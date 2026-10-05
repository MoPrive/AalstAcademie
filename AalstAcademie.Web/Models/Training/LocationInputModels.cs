// Sprint 003: Bindt alleen zaalnaam, adres, maximum en bij edit/delete de oude versie
// Ruwe tekst bewaart ook ongeldige getallen voor foutweergave.
using System.ComponentModel.DataAnnotations;

namespace AalstAcademie.Web.Models.Training;

/// <summary>Ruwe scalars behouden ongeldige invoer; bindlijsten sluiten technische en momentvelden uit.</summary>
public class LocationCreateInput
{
    public const string BindCreate = "Name,Address,MaximumCapacity";
    [Display(Name = "Zaalnaam")] public string? Name { get; set; }
    [Display(Name = "Adres")] public string? Address { get; set; }
    [Display(Name = "Zaalcapaciteit")] public string? MaximumCapacity { get; set; }
}
public class LocationEditInput : LocationCreateInput
{
    public const string BindEdit = BindCreate + ",ExpectedVersion";
    public string? ExpectedVersion { get; set; }
}
/// <summary>Verwijdering vereist alleen de bewust gelezen versie; referenties worden atomair opnieuw getoetst.</summary>
public class LocationDeleteInput
{
    public const string BindDelete = "ExpectedVersion";
    public string? ExpectedVersion { get; set; }
}
