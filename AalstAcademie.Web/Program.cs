// Sprint 003: Registreert de scoped featurediensten, actuele policies en veilige demo-bootstrap vóór HTTP
// Gepensioneerde archive/restore/delete-paden geven 404; de gebruikersapp is voor deze opdracht niet herstart.
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using AalstAcademie.Web.Data;
using AalstAcademie.Web.Models.Identity;
using AalstAcademie.Web.Services.Accounts;
using AalstAcademie.Web.Services.Training;
using AalstAcademie.Web.Configuration;
using AalstAcademie.Web.Security;
using AalstAcademie.Web.Hubs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

// Samenstelpunt: account- en opleidingsservices, Identity, toegangspolicies en HTTP-routes.
var builder = WebApplication.CreateBuilder(args);

// Valideer de demogrens vóór migratie/seed: een Development-vlag mag nooit productie activeren.
var demoMode = DemoMode.From(builder.Environment, builder.Configuration);
builder.Services.AddSingleton(demoMode);
builder.Services.AddSingleton(TimeProvider.System);

// Resolveer relatief SQLite-doel uitsluitend tegen ContentRoot en valideer de demoresetgrens vóór writes.
var demoTarget = DemoDatabaseTarget.From(builder.Environment, builder.Configuration, demoMode);
builder.Services.AddSingleton(demoTarget);
builder.Services.AddSingleton<DemoCredentials>();
// De lease behoort aan de hostlevensduur en wordt pas na tijdzonevalidatie door bootstrap opgevraagd.
builder.Services.AddSingleton<DemoDatabaseLease>(provider => DemoDatabaseLease.Acquire(provider.GetRequiredService<DemoDatabaseTarget>()));
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(demoTarget.ConnectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

// Alle Identity-managers en EF-stores gebruiken hetzelfde ApplicationUser-type.
// Demo heeft geen mailintegratie; administratieve goedkeuring blijft een afzonderlijke toegangspoort.
// Buiten demo blijft de oorspronkelijke bevestigingsvoorwaarde behouden.
builder.Services.AddDefaultIdentity<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = !demoMode.IsEnabled;
        options.SignIn.RequireConfirmedEmail = false;
        options.User.RequireUniqueEmail = true;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager<ApplicationSignInManager>();
// De kleine demo verifieert stamps ieder verzoek; oude sessies worden afgewezen, niet bijgewerkt.
builder.Services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);
builder.Services.ConfigureApplicationCookie(options =>
{
    // De cookiecontrole leest de huidige accounttoestand; eerder verleende rechten zijn geen blijvend bewijs.
    options.Events.OnValidatePrincipal = AccountCookieValidation.ValidateAsync;
    options.Events.OnRedirectToLogin = context =>
    {
        // Een snapshot/hub verwacht een statuscode; een login-HTML-pagina zou daar een ongeldig antwoord zijn.
        if (AccountAuthorizationResultHandler.IsApiRequest(context.Request)) context.Response.StatusCode = 401;
        else context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        // 403 betekent dat de identiteit bekend is, maar de huidige toegang niet voldoende is.
        if (AccountAuthorizationResultHandler.IsApiRequest(context.Request) || context.Request.Path.StartsWithSegments("/RegistrationManagement")) context.Response.StatusCode = 403;
        else context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
});
// Scoped: per HTTP-verzoek/expliciete scope één service met de scoped databasecontext.
builder.Services.AddScoped<InstructorProfileService>();
builder.Services.AddScoped<DatabaseInitializer>();
builder.Services.AddScoped<DemoDataSeeder>();
// De volledige aanbodbatch moet slagen voordat bootstrap HTTP en de tijdelijke loginhint vrijgeeft.
builder.Services.AddScoped<DemoOfferSeeder>();
// Sprint 004: deelnamevoorbeelden moeten eveneens slagen voordat de host gereedheid publiceert.
builder.Services.AddScoped<DemoEnrolmentSeeder>();
builder.Services.AddScoped<DemoParticipationWorkflowSeeder>();
builder.Services.AddScoped<DemoDatabaseBootstrapper>();
builder.Services.AddScoped<AccountRegistrationService>();
builder.Services.AddScoped<AccountReviewService>();
builder.Services.AddScoped<AccountApplicationQueries>();
// Sprint 004: dezelfde scoped context verbindt actuele rechten, capaciteit en beperkte accountkoppelingen.
builder.Services.AddScoped<AccountManagerChoices>();
builder.Services.AddScoped<AccountManagerService>();
builder.Services.AddScoped<DepartmentResponsibleQueries>();
builder.Services.AddScoped<DepartmentResponsibleService>();
builder.Services.AddScoped<AalstAcademie.Web.Services.Enrolment.EmployeeAccessReader>();
builder.Services.AddScoped<AalstAcademie.Web.Services.Enrolment.ParticipationEligibility>();
builder.Services.AddScoped<AalstAcademie.Web.Services.Enrolment.EmployeeCatalogueQueries>();
builder.Services.AddScoped<AalstAcademie.Web.Services.Enrolment.EmployeeEnrolmentQueries>();
builder.Services.AddScoped<AalstAcademie.Web.Services.Enrolment.EmployeeEnrolmentService>();
builder.Services.AddScoped<AalstAcademie.Web.Services.Enrolment.WaitlistPromotionService>();
builder.Services.AddScoped<AalstAcademie.Web.Services.Enrolment.RegistrationManagementQueries>();
builder.Services.AddScoped<AalstAcademie.Web.Services.Enrolment.RegistrationReviewService>();
builder.Services.AddScoped<AalstAcademie.Web.Services.Enrolment.RegistrationCancellationService>();
builder.Services.AddScoped<AalstAcademie.Web.Services.Enrolment.TrainingMomentCancellationService>();
builder.Services.AddScoped<IAccountApplicationNotifier, SignalRAccountApplicationNotifier>();
// Opleidingsbeheer deelt de scoped context; de klok blijft vervangbaar in geïsoleerde tests.
builder.Services.AddScoped<TrainingSchedule>(provider => new TrainingSchedule(provider.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped<TrainingValueValidation>();
builder.Services.AddScoped<TrainingAccessReader>();
builder.Services.AddScoped<TrainingWriteTransaction>();
builder.Services.AddScoped<TrainingManagementService>();
builder.Services.AddScoped<CategoryManagementService>();
builder.Services.AddScoped<TrainingManagementQueries>();
// Ook bestaande opleidingdetails lezen de nieuwe moment/foundation-shape al vóór nieuwe schermen in fase B.
builder.Services.AddScoped<TrainingFoundationQueries>();
builder.Services.AddScoped<TrainingMomentManagementService>();
builder.Services.AddScoped<TrainingMomentManagementQueries>();
builder.Services.AddScoped<LocationManagementService>();
builder.Services.AddScoped<LocationManagementQueries>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentAccountAccessor>();
builder.Services.AddScoped<IAuthorizationHandler, AccountAccessHandler>();
builder.Services.AddScoped<IAuthorizationHandler, TrainingManagementHandler>();
// Sprint 004: de nieuwe HTML-routes geven begrensd 403; bestaande account/API-afhandeling wordt gedelegeerd.
builder.Services.AddScoped<IAuthorizationHandler, EmployeeParticipationHandler>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, EmployeeAuthorizationResultHandler>();
builder.Services.AddAuthorization(options =>
{
    // Zowel expliciet [Authorize] als routes zonder eigen policy vereisen standaard een goedgekeurd account.
    var approved = new AuthorizationPolicyBuilder().RequireAuthenticatedUser()
        .AddRequirements(new AccountAccessRequirement(RequireApproval: true)).Build();
    options.DefaultPolicy = approved;
    options.FallbackPolicy = approved;
    // Alleen het eigen statusgebied gebruikt de smallere policy voor een nog niet goedgekeurde aanvrager.
    options.AddPolicy(AccountPolicies.AuthenticatedAccount, policy => policy.RequireAuthenticatedUser()
        .AddRequirements(new AccountAccessRequirement()));
    options.AddPolicy(AccountPolicies.ApprovedAccount, approved);
    // Beheer vereist daarbovenop de actuele administratorrol, gecontroleerd vanuit de database.
    options.AddPolicy(AccountPolicies.ApprovedAdministrator, policy => policy.RequireAuthenticatedUser()
        .AddRequirements(new AccountAccessRequirement(RequireApproval: true, RequireAdministrator: true)));
    // Een goedgekeurd account alleen is geen Lesgeverrecht; beide controles lezen actuele opslag.
    options.AddPolicy(TrainingPolicies.TrainingManagement, policy => policy.RequireAuthenticatedUser()
        .AddRequirements(new AccountAccessRequirement(RequireApproval: true), new TrainingManagementRequirement()));
    options.AddPolicy(EmployeePolicies.EmployeeParticipation, policy => policy.RequireAuthenticatedUser()
        .AddRequirements(new AccountAccessRequirement(RequireApproval: true), new EmployeeParticipationRequirement()));
});
// De hub verstuurt een wijzigingssignaal; beveiligde queries leveren daarna de werkelijke gegevens.
builder.Services.AddSignalR();
builder.Services.AddControllersWithViews();

// Ook een fout vóór Run moet eigen resources, waaronder de lifetimelease, vrijgeven.
await using var app = builder.Build();

// Opstart valt buiten een HTTP-verzoek. Een eigen scope geeft DbContext/RoleManager
// de juiste levensduur en ruimt ze na migratie/referentie-initialisatie weer op.
// Dit werkt op de geconfigureerde database; tests/smokechecks kiezen een tijdelijke database.
await using (var scope = app.Services.CreateAsyncScope())
{
    // Ontbrekende Belgische zonedata zijn een configuratiefout vóór migratie of seed, nooit een UTC-default.
    _ = scope.ServiceProvider.GetRequiredService<TrainingSchedule>();
    await scope.ServiceProvider.GetRequiredService<DemoDatabaseBootstrapper>().InitializeAsync();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

// Unsupported Identity-pagina's mogen ook via directe GET/POST geen mail/token/Manage-logica uitvoeren.
app.UseMiddleware<IdentityEndpointAvailabilityMiddleware>();

// Eerst de identiteit uit de login-cookie bepalen, daarna toegangsregels beoordelen.
app.UseAuthentication();
app.UseAuthorization();

// Gepensioneerde acties krijgen voor ieder HTTP-werkwoord dezelfde 404, zonder nieuw endpoint of database-read.
// Static-assets fallback kan voor een onbekend werkwoord anders 405 geven; dit bewaart het expliciete retired-contract.
app.Use(async (context, next) =>
{
    if (new[] { "/TrainingManagement/Archive", "/TrainingManagement/Restore", "/TrainingManagement/Delete" }
        .Any(path => context.Request.Path.StartsWithSegments(path, StringComparison.OrdinalIgnoreCase)))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await next(context);
});

// Ook login en registratie hebben styles/scripts nodig voordat iemand een account heeft.
app.MapStaticAssets().AllowAnonymous();

// De bestaande Identity-cookie identificeert de beheerder; de hub biedt geen clientmutaties.
app.MapHub<AccountApplicationsHub>("/hubs/account-applications", options => options.CloseOnAuthenticationExpiration = true);

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.MapRazorPages()
   .WithStaticAssets();

app.Run();

// WebApplicationFactory kan de echte middleware en Identity-pagina's met geïsoleerde testdata hosten.
public partial class Program { }
