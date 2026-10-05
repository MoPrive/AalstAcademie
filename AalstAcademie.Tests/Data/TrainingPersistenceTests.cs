// Sprint 003: Toetst de opleidingsdefinitie, account-eigenaar, categorie, doelgroep en opslaggrenzen rechtstreeks op SQLite
// Raw SQL bewijst de vangrail buiten servicevalidatie.
using System.Text;
using System.Data;
using AalstAcademie.Tests.Infrastructure;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Domain;
using AalstAcademie.Web.Models.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TrainingEntity = AalstAcademie.Web.Models.Domain.Training;

namespace AalstAcademie.Tests.Data;

/// <summary>
/// Vijftien providergevallen bewijzen de nieuwe definitie-opslag. Raw SQL omzeilt
/// services bewust; elke testcase gebruikt uitsluitend zijn eigen geheugenfixture.
/// </summary>
public class TrainingPersistenceTests
{
    // De drie prijsvormen moeten exact als null of Int64-centen terugkomen, zonder REAL of afronding.
    [Theory]
    [InlineData("internal")]
    [InlineData("external-zero")]
    [InlineData("external-maximum")]
    public async Task Converted_price_and_owner_roundtrip_exactly(string variant)
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var external = variant != "internal";
        var price = variant == "external-maximum" ? TrainingValueConverters.MaximumExternalTotalPriceEuros : 0m;
        var prepared = await PrepareAsync(db, external, price);
        db.Context.ChangeTracker.Clear();
        var stored = await db.Context.Trainings.AsNoTracking().Include(x => x.TrainingDepartments)
            .SingleAsync(x => x.Id == prepared.Training.Id);
        Assert.Equal(prepared.Training.InstructorUserId, stored.InstructorUserId);
        Assert.Equal(external ? (decimal?)price : null, stored.ExternalTotalPriceEuros);
        Assert.Equal(prepared.Training.Version, stored.Version);
        Assert.Equal(TrainingAudienceScope.SelectedDepartments, stored.AudienceScope);
        Assert.Equal(prepared.TargetDepartmentId, Assert.Single(stored.TrainingDepartments).DepartmentId);
        await using var command = db.Context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT ExternalTotalPriceCents, typeof(ExternalTotalPriceCents), InstructorUserId FROM Trainings WHERE Id=@id";
        command.Parameters.Add(new SqliteParameter("@id", stored.Id));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        if (external) { Assert.Equal(variant == "external-maximum" ? long.MaxValue : 0L, reader.GetInt64(0)); Assert.Equal("integer", reader.GetString(1)); }
        else { Assert.True(reader.IsDBNull(0)); Assert.Equal("null", reader.GetString(1)); }
        Assert.Equal(stored.InstructorUserId, reader.GetString(2));
    }

    // Lokale guards bewaken tekst/scalars; ook een geldig Guid-prefix met verborgen NUL-suffix is ongeldig.
    // Profiel/prijscompatibiliteit is na de account-FK een serverregel.
    [Fact]
    public async Task Definition_scalars_and_nullable_integer_cents_are_physically_guarded()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var prepared = await PrepareAsync(db);
        foreach (var value in new object?[] { null, " ", new string('x', 201), "a\0b" })
            await RejectAsync(db, "Trainings", prepared.Training.Id, "Title", value);
        foreach (var description in new[] { new string('x', 4001), "tekst\0suffix", "tekst\0" + new string('x', 4001) })
            await RejectAsync(db, "Trainings", prepared.Training.Id, "Description", description);
        foreach (var value in new object?[] { null, "", " \t", "a\0b" })
            await RejectAsync(db, "Trainings", prepared.Training.Id, "InstructorUserId", value);
        foreach (var value in new object[] { -1, 3, 1.5, "invalid" })
            await RejectAsync(db, "Trainings", prepared.Training.Id, "AudienceScope", value);
        foreach (var value in new object[] { -1, 2, 0.5 })
            await RejectAsync(db, "Trainings", prepared.Training.Id, "RequiresMotivation", value);
        foreach (var value in new object?[] { null, "", "invalid", Guid.Empty.ToString(), Guid.NewGuid().ToString() + "\0suffix" })
            await RejectAsync(db, "Trainings", prepared.Training.Id, "Version", value);
        foreach (var value in new object[] { -1L, 0.125, "invalid" })
            await RejectAsync(db, "Trainings", prepared.Training.Id, "ExternalTotalPriceCents", value);
        // De database kan uit één UserID-FK het type niet afleiden: positieve centen en null zijn lokaal geldig.
        await db.Context.Database.ExecuteSqlRawAsync("UPDATE Trainings SET ExternalTotalPriceCents=1 WHERE Id=@id", new SqliteParameter("@id", prepared.Training.Id));
        Assert.Equal(0.01m, await db.Context.Trainings.AsNoTracking().Select(x => x.ExternalTotalPriceEuros).SingleAsync());
        await db.Context.Database.ExecuteSqlRawAsync("UPDATE Trainings SET ExternalTotalPriceCents=NULL WHERE Id=@id", new SqliteParameter("@id", prepared.Training.Id));
        Assert.Null(await db.Context.Trainings.AsNoTracking().Select(x => x.ExternalTotalPriceEuros).SingleAsync());
        Assert.Equal(prepared.Training.Version, await db.Context.Trainings.AsNoTracking().Select(x => x.Version).SingleAsync());
        var properties = db.Context.Model.FindEntityType(typeof(TrainingEntity))!.GetProperties().Select(x => x.Name).ToArray();
        foreach (var retired in new[] { "Date", "StartTime", "EndTime", "Location", "MaximumParticipants", "InternalInstructorId", "ExternalInstructorId", "IsArchived" })
            Assert.DoesNotContain(retired, properties);
    }

    // De bestaande technische Unicode-sleutel blijft uniek; de raw writer krijgt geen normalisatiereparatie.
    [Fact]
    public async Task Category_constraints_and_normalized_unique_index_remain()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var prepared = await PrepareAsync(db);
        foreach (var column in new[] { "Name", "NormalizedName" })
            foreach (var invalid in new object?[] { null, " ", new string('x', 101), "a\0b" })
                await RejectAsync(db, "Categories", prepared.Training.CategoryId, column, invalid);
        foreach (var invalid in new object?[] { null, "", Guid.Empty.ToString(), Guid.NewGuid().ToString() + "\0suffix" })
            await RejectAsync(db, "Categories", prepared.Training.CategoryId, "Version", invalid);
        var normalized = await db.Context.Categories.AsNoTracking().Select(x => EF.Property<string>(x, "NormalizedName")).SingleAsync();
        var error = await Assert.ThrowsAsync<SqliteException>(() => db.Context.Database.ExecuteSqlRawAsync(
            "INSERT INTO Categories(Name,NormalizedName,Version) VALUES('Andere naam',@name,@version)",
            new SqliteParameter("@name", normalized), new SqliteParameter("@version", Guid.NewGuid().ToString())));
        Assert.Equal(2067, error.SqliteExtendedErrorCode);
        Assert.Single(await db.Context.Categories.AsNoTracking().ToListAsync());
    }

    // Dezelfde afdeling kan slechts één keer in de volledige linkset van dezelfde Training voorkomen.
    [Fact]
    public async Task Duplicate_training_department_fails_composite_primary_key()
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var prepared = await PrepareAsync(db);
        var error = await Assert.ThrowsAsync<SqliteException>(() => db.Context.Database.ExecuteSqlRawAsync(
            "INSERT INTO TrainingDepartments(TrainingId,DepartmentId) VALUES(@training,@department)",
            new SqliteParameter("@training", prepared.Training.Id), new SqliteParameter("@department", prepared.TargetDepartmentId)));
        Assert.Equal(1555, error.SqliteExtendedErrorCode);
        Assert.Single(await db.Context.TrainingDepartments.AsNoTracking().ToListAsync());
    }

    // Elk van de vier fysieke verwijzingen wordt met verder geldige waarden onafhankelijk uitgedaagd.
    [Theory]
    [InlineData("category")]
    [InlineData("instructor")]
    [InlineData("training-department")]
    [InlineData("department")]
    public async Task Unknown_required_reference_fails_foreign_key(string relationship)
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var prepared = await PrepareAsync(db);
        SqliteException error;
        if (relationship is "training-department" or "department")
            error = await Assert.ThrowsAsync<SqliteException>(() => db.Context.Database.ExecuteSqlRawAsync(
                "INSERT INTO TrainingDepartments(TrainingId,DepartmentId) VALUES(@training,@department)",
                new SqliteParameter("@training", relationship == "training-department" ? 999999 : prepared.Training.Id),
                new SqliteParameter("@department", relationship == "department" ? 999999 : prepared.TargetDepartmentId)));
        else error = await RejectAsync(db, "Trainings", prepared.Training.Id,
            relationship == "category" ? "CategoryId" : "InstructorUserId", relationship == "category" ? 999999 : "missing-user");
        Assert.Equal(787, error.SqliteExtendedErrorCode);
        Assert.Equal(prepared.Training.InstructorUserId, await db.Context.Trainings.AsNoTracking().Select(x => x.InstructorUserId).SingleAsync());
        Assert.Single(await db.Context.TrainingDepartments.AsNoTracking().ToListAsync());
    }

    // De eigenaar heeft hier geen profiel: juist de nieuwe Training/account-FK moet de delete tegenhouden.
    [Theory]
    [InlineData("category")]
    [InlineData("user")]
    [InlineData("department")]
    public async Task Referenced_parent_delete_is_restricted(string relationship)
    {
        await using var db = await SqliteTestDatabase.CreateAsync();
        var prepared = await PrepareAsync(db);
        var (sql, id) = relationship switch
        {
            "category" => ("DELETE FROM Categories WHERE Id=@id", (object)prepared.Training.CategoryId),
            "user" => ("DELETE FROM AspNetUsers WHERE Id=@id", (object)prepared.Training.InstructorUserId),
            _ => ("DELETE FROM Departments WHERE Id=@id", (object)prepared.TargetDepartmentId)
        };
        var error = await Assert.ThrowsAsync<SqliteException>(() => db.Context.Database.ExecuteSqlRawAsync(sql, new SqliteParameter("@id", id)));
        Assert.Equal(19, error.SqliteErrorCode);
        Assert.Single(await db.Context.Trainings.AsNoTracking().ToListAsync());
        Assert.Single(await db.Context.TrainingDepartments.AsNoTracking().ToListAsync());
    }

    // Tooling zonder expliciet CLI-doel mag niet op defaultconfiguratie of app.db terugvallen.
    [Fact]
    public void Factory_without_explicit_connection_refuses()
    {
        var factory = new ApplicationDbContextFactory();
        Assert.Throws<InvalidOperationException>(() => factory.CreateDbContext([]));
        Assert.Throws<InvalidOperationException>(() => factory.CreateDbContext(["--ConnectionStrings:DefaultConnection", " "]));
    }

    // Modelconstructie vereist geen open verbinding of bestand; de runtime en designfactory blijven gelijk.
    [Fact]
    public async Task Factory_isolated_memory_and_file_models_match_without_startup()
    {
        await using var db = await SqliteTestDatabase.CreateAsync(migrate: false);
        var directory = Path.Combine(Path.GetTempPath(), $"AalstAcademie-sprint003-factory-{Guid.NewGuid():N}");
        var file = Path.Combine(directory, "design-only.db");
        var factory = new ApplicationDbContextFactory();
        await using var memory = factory.CreateDbContext(["--ConnectionStrings:DefaultConnection", "Data Source=:memory:;Foreign Keys=True;Pooling=False"]);
        await using var onDisk = factory.CreateDbContext(["--ConnectionStrings:DefaultConnection", $"Data Source={file};Foreign Keys=True;Pooling=False"]);
        Assert.Equal(ModelSignature(db.Context), ModelSignature(memory));
        Assert.Equal(ModelSignature(db.Context), ModelSignature(onDisk));
        Assert.Equal(ConnectionState.Closed, memory.Database.GetDbConnection().State);
        Assert.Equal(ConnectionState.Closed, onDisk.Database.GetDbConnection().State);
        Assert.False(Directory.Exists(directory));
        Assert.False(File.Exists(file));
    }

    /// <summary>Een profielvrije eigenaar is genoeg voor fysieke referentie-integriteit; diensten toetsen geschiktheid.</summary>
    private static async Task<PreparedTraining> PrepareAsync(SqliteTestDatabase db, bool external = false, decimal price = 0m)
    {
        var owner = await db.AddUserAsync(external ? RequestedAccountType.ExternalInstructor : RequestedAccountType.InternalInstructor);
        var category = new Category { Name = $"Categorie {Guid.NewGuid():N}" };
        db.Context.Categories.Add(category);
        db.Context.Entry(category).Property<string>("NormalizedName").CurrentValue = category.Name.ToUpperInvariant();
        db.Context.Entry(category).Property<Guid>("Version").CurrentValue = Guid.NewGuid();
        var target = new Department { Name = $"Doelgroep {Guid.NewGuid():N}" };
        db.Context.Departments.Add(target);
        await db.Context.SaveChangesAsync();
        var training = new TrainingEntity { Title = "Fictieve opleiding", Description = "Gewone tekst\n<script>blijft tekst</script>",
            CategoryId = category.Id, InstructorUserId = owner.Id, ExternalTotalPriceEuros = external ? price : null,
            AudienceScope = TrainingAudienceScope.SelectedDepartments, RequiresMotivation = true };
        training.TrainingDepartments.Add(new TrainingDepartment { DepartmentId = target.Id });
        db.Context.Trainings.Add(training);
        await db.Context.SaveChangesAsync();
        return new(training, target.Id);
    }

    /// <summary>SQL-identifiers komen alleen uit deze lokale allowlist; alle variabele waarden blijven parameters.</summary>
    private static async Task<SqliteException> RejectAsync(SqliteTestDatabase db, string table, int id, string column, object? value)
    {
        var allowed = table == "Trainings" ? new[] { "Title", "Description", "CategoryId", "InstructorUserId", "ExternalTotalPriceCents", "AudienceScope", "RequiresMotivation", "Version" }
            : table == "Categories" ? new[] { "Name", "NormalizedName", "Version" } : [];
        if (!allowed.Contains(column, StringComparer.Ordinal)) throw new ArgumentOutOfRangeException(nameof(column));
        // Alleen NUL-tekst gaat als volledige UTF-8-bytebuffer binnen: gewone tekstbinding kan het suffix verliezen.
        // Een onafhankelijke hex-readback bewijst eerst dat SQLite echt de volledige ongeldige tekst ontvangt.
        var valueParameter = new SqliteParameter("@value", value ?? DBNull.Value);
        var valueExpression = "@value";
        if (value is string nulText && nulText.Contains('\0'))
        {
            var bytes = Encoding.UTF8.GetBytes(nulText);
            valueParameter = new SqliteParameter("@value", SqliteType.Blob) { Value = bytes };
            Assert.Equal(SqliteType.Blob, valueParameter.SqliteType);
            await using var probe = db.Context.Database.GetDbConnection().CreateCommand();
            probe.CommandText = "SELECT typeof(@value), typeof(CAST(@value AS TEXT)), hex(CAST(@value AS TEXT))";
            probe.Parameters.Add(new SqliteParameter("@value", SqliteType.Blob) { Value = bytes });
            await using var reader = await probe.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("blob", reader.GetString(0));
            Assert.Equal("text", reader.GetString(1));
            Assert.Equal(Convert.ToHexString(bytes), reader.GetString(2));
            valueExpression = "CAST(@value AS TEXT)";
        }
        // Identifiers komen uit de allowlist; de SQL-expressie heeft twee vaste vormen en alle waarden blijven parameters.
        var sql = $"UPDATE {table} SET {column}={valueExpression} WHERE Id=@id";
        var error = await Assert.ThrowsAsync<SqliteException>(() => db.Context.Database.ExecuteSqlRawAsync(
            sql, valueParameter, new SqliteParameter("@id", id)));
        Assert.Equal(19, error.SqliteErrorCode);
        return error;
    }

    private static string ModelSignature(ApplicationDbContext context) => string.Join("\n", context.Model.GetEntityTypes().OrderBy(x => x.Name)
        .Select(entity => entity.Name + ":" + entity.GetTableName() + ":" + string.Join("|", entity.GetProperties().OrderBy(x => x.Name)
            .Select(property => $"{property.Name},{property.ClrType.FullName},{property.IsNullable},{property.IsConcurrencyToken},{property.GetColumnType()}"))));
    private sealed record PreparedTraining(TrainingEntity Training, int TargetDepartmentId);
}
