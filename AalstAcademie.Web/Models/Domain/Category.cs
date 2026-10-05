// Sprint 003: Bevat de zichtbare categorie-identiteit en naam
// NormalizedName en Version zijn EF-shadowproperties, zodat technische uniciteit en CAS buiten de publieke entiteitvorm blijven.
namespace AalstAcademie.Web.Models.Domain;

/// <summary>
/// Een beheerde opleidingscategorie. De technische genormaliseerde naam en Guid-versie
/// zijn EF-shadowproperties, zodat zij niet als vrije businessvelden gebonden worden.
/// </summary>
public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    // Hernoemen bewaart deze koppelingen; categoriebeheer verwijdert geen opleidingen.
    public ICollection<Training> Trainings { get; set; } = new List<Training>();
}
