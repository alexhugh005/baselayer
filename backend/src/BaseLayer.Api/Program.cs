using Microsoft.AspNetCore.DataProtection;
using BaseLayer.Api.Providers;
using BaseLayer.Api.Workers;
using BaseLayer.Api.Authentication;
using BaseLayer.Api.Middleware;
using BaseLayer.Application.Interfaces;
using BaseLayer.Application.Services;
using BaseLayer.Data;
using BaseLayer.Data.Repositories;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false).AddEnvironmentVariables();
var local = builder.Configuration.GetValue<bool>("LocalDemo:Enabled");
var origins = builder.Configuration.GetSection("Clerk:AuthorizedParties").Get<string[]>() ?? ["http://localhost:5173"];
if (local && (!builder.Environment.IsDevelopment() || (builder.Configuration["LocalDemo:Token"]?.Length ?? 0) < 32))
    throw new InvalidOperationException("Local demo requires Development and a token of at least 32 characters.");
if (local)
{
    // Local demo authentication must never listen on external interfaces.
    builder.WebHost.UseUrls($"http://127.0.0.1:{builder.Configuration.GetValue<int?>("LocalDemo:Port") ?? 5080}");
    builder.Services.AddAuthentication("Local").AddScheme<AuthenticationSchemeOptions, LocalAuthenticationHandler>("Local", _ => { });
}
else
{
    var issuer = builder.Configuration["Clerk:Issuer"] ?? throw new InvalidOperationException("Configure Clerk:Issuer or explicitly enable local development mode.");
    if (!Uri.TryCreate(issuer, UriKind.Absolute, out var uri) || uri.Scheme != "https")
        throw new InvalidOperationException("Clerk issuer must use HTTPS.");
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
    {
        o.Authority = issuer;
        o.MapInboundClaims = false;
        o.RequireHttpsMetadata = true;
        o.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, ValidIssuer = issuer, ValidateAudience = false, ValidateLifetime = true, ValidateIssuerSigningKey = true, RequireSignedTokens = true, ClockSkew = TimeSpan.FromSeconds(5), ValidAlgorithms = [SecurityAlgorithms.RsaSha256] };
        o.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
        {
            var azp = context.Principal?.FindFirst("azp")?.Value;
            if (string.IsNullOrWhiteSpace(context.Principal?.FindFirst("sub")?.Value) || azp is null || !origins.Contains(azp, StringComparer.Ordinal))
                context.Fail("Untrusted session origin.");
            return Task.CompletedTask;
        }
        };
    });
}
builder.Services.AddAuthorization();
builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));
var connection = builder.Configuration.GetConnectionString("Default") ?? "Data Source=data/base-layer-oauth.db";
Directory.CreateDirectory("data");
builder.Services.AddDbContext<PlatformDbContext>(o => o.UseSqlite(connection));
builder.Services.AddSingleton<HomeOperationGate>();
builder.Services.AddSingleton<DatabaseGate>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IPlatformRepository, PlatformRepository>();
builder.Services.AddScoped<IPlatformService, PlatformService>();
builder.Services.AddSingleton<IUsageLimitReachedService, UsageLimitReachedService>();
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo("data/keys")).SetApplicationName("BaseLayer");
builder.Services.AddSingleton<ICredentialProtector, CredentialProtector>();
builder.Services.AddHttpClient<ISmartHomeProvider, HomeAssistantProvider>(client => client.Timeout = TimeSpan.FromSeconds(10)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHostedService<HomePollingWorker>();
var app = builder.Build();
using (var scope = app.Services.CreateScope()) await DatabaseInitializer.InitializeAsync(scope.ServiceProvider.GetRequiredService<PlatformDbContext>());
app.UseMiddleware<ExceptionMiddleware>();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health", () => Results.Ok(new { status = "healthy", authentication = local ? "local-development" : "clerk" }));
app.MapControllers();
app.Run();
public partial class Program;
