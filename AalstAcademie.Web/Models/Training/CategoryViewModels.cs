// Sprint 003: Scheidt beperkte categorie-invoer van schermfeedback en leesresultaten
// Hernoemen post de gelezen versie; een fout vervangt die versie niet automatisch.
using System.ComponentModel.DataAnnotations;

namespace AalstAcademie.Web.Models.Training;

/// <summary>Categorieaanmaak bindt alleen de naam; normalisatie en opslagversie komen uit de service.</summary>
public class CategoryCreateInput
{
    public const string BindCreate = "Name";
    [Display(Name = "Naam")] public string? Name { get; set; }
}

/// <summary>Een rename moet de bewust gelezen shadowversie meesturen, zonder overige opslagvelden.</summary>
public class CategoryEditInput : CategoryCreateInput
{
    public const string BindEdit = "Name,ExpectedVersion";
    public string? ExpectedVersion { get; set; }
}

/// <summary>De actuele administratieve lijst bevat geen delete- of opleidingsmutatiecontract.</summary>
public sealed record CategoryIndexViewModel(IReadOnlyList<TrainingCategoryReadModel> Categories);

/// <summary>Ook naamfouten behouden raw invoer en veilige uitleg.</summary>
public sealed class CategoryCreateViewModel : CategoryCreateInput
{
    public bool IsConflict { get; set; }
    public string? Message { get; set; }
}

/// <summary>Bij conflict blijft het oorspronkelijke token staan; alleen expliciet herlezen haalt een nieuwe versie op.</summary>
public sealed class CategoryEditViewModel : CategoryEditInput
{
    public int Id { get; init; }
    public bool IsConflict { get; set; }
    public string? Message { get; set; }
}
