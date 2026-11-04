using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Triage.Api.Data;
using Triage.Api.Services;

var builder = WebApplication.CreateBuilder(args);

var dataDir = Path.GetFullPath(builder.Configuration["DataDir"] ?? "App_Data");
Directory.CreateDirectory(dataDir);

builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 1L << 30);
builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
    o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// Database provider is configuration-driven: Sqlite (default) | SqlServer | Postgres.
var provider = builder.Configuration["Database:Provider"] ?? "Sqlite";
var connection = builder.Configuration.GetConnectionString("Default") ?? $"Data Source={Path.Combine(dataDir, "triage.db")}";
builder.Services.AddDbContext<AppDb>(o =>
{
    switch (provider.ToLowerInvariant())
    {
        case "sqlserver": o.UseSqlServer(connection); break;
        case "postgres": case "postgresql": o.UseNpgsql(connection); break;
        default: o.UseSqlite(connection); break;
    }
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IntegrationConfig>();
builder.Services.AddSingleton<JiraDossier>();
builder.Services.AddSingleton<AiOptions>();
builder.Services.AddSingleton<UsageRecorder>();
builder.Services.AddSingleton<BudgetService>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<ArtifactStore>();
builder.Services.AddHttpClient<AnthropicService>(c => c.Timeout = TimeSpan.FromMinutes(3));
builder.Services.AddHttpClient<JiraService>(c => c.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddScoped<TriageService>();

var tokens = new TokenService(builder.Configuration);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = "triage", ValidAudience = "triage", IssuerSigningKey = tokens.Key,
        ClockSkew = TimeSpan.FromMinutes(1)
    };
    o.Events = new JwtBearerEvents
    {
        // Deactivated or deleted users lose access immediately instead of when the token expires.
        OnTokenValidated = async ctx =>
        {
            var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDb>();
            var id = Guid.Parse(ctx.Principal!.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (u is not { IsActive: true }) ctx.Fail("User is inactive.");
        }
    };
});
builder.Services.AddAuthorization();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    db.Database.EnsureCreated(); // swap for EF migrations once the schema needs to evolve on a shared DB
    SchemaUpgrades.EnsureBugJiraColumns(db, provider);
    SchemaUpgrades.EnsureStoryColumns(db, provider);
    AiSchema.Ensure(db, provider); // EnsureCreated skips databases that already exist, so add the AI usage tables here
    if (!db.Users.Any())
    {
        var email = (Environment.GetEnvironmentVariable("ADMIN_EMAIL") ?? "admin@triage.local").ToLowerInvariant();
        var password = Environment.GetEnvironmentVariable("ADMIN_PASSWORD") ?? "Admin@12345";
        var admin = new AppUser { Email = email, DisplayName = "Administrator", Role = Roles.Admin, MustChangePassword = true };
        admin.PasswordHash = new PasswordHasher<AppUser>().HashPassword(admin, password);
        db.Users.Add(admin);
        db.SaveChanges();
        app.Logger.LogWarning("Seeded initial admin {Email}. You will be asked to change the password at first login.", email);
    }
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapFallbackToFile("index.html");

app.Run();
