using COMS_MVC.Data;
using COMS_MVC.Hubs;
using COMS_MVC.Models;
using COMS_MVC.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Railway (and similar hosts) inject PORT — listen on 0.0.0.0:$PORT.
// Locally PORT is unset, so launchSettings.json (http://localhost:5070) applies.
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

// TLS ends at Railway's proxy — honor X-Forwarded-* headers.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Data Source=coms.db";

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddIdentity<ApplicationUser, IdentityRole<int>>(options =>
{
    options.Password.RequireDigit = true;
    options.Password.RequiredLength = 6;
    options.Password.RequireNonAlphanumeric = true;
    options.Password.RequireUppercase = true;
    // LGU demo password (LGU@COMS123!) is uppercase-only by requirement, so lowercase
    // cannot be mandatory at the Identity level. Registration forms may still enforce
    // a stricter mixed-case rule on top of this baseline.
    options.Password.RequireLowercase = false;
    options.Password.RequiredUniqueChars = 1;
    options.SignIn.RequireConfirmedAccount = false;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
    options.Lockout.MaxFailedAccessAttempts = 5;
})
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddSignInManager()
.AddRoles<IdentityRole<int>>()
.AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Dashboard/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromDays(7);
    options.SlidingExpiration = true;
});

builder.Services.Configure<SimulationOptions>(builder.Configuration.GetSection("DemoSettings"));

// Gzip/Brotli for text responses (HTML, CSS, JS, JSON, SVG). No behavior
// change — the middleware only encodes bytes on the wire.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
});

// Email via Brevo HTTPS API (port 443 — works where SMTP ports are blocked).
// Production (Railway) provides: Brevo__ApiKey, Brevo__SenderEmail.
// Local dev may still use the legacy shape: Email:ApiKey / Email:SenderEmail
// (user-secrets), kept as a fallback. Never commit a real key.
builder.Services.Configure<BrevoOptions>(o =>
{
    builder.Configuration.GetSection("Brevo").Bind(o);
    if (string.IsNullOrWhiteSpace(o.ApiKey))
        o.ApiKey = builder.Configuration["Email:ApiKey"] ?? string.Empty;
    if (string.IsNullOrWhiteSpace(o.SenderEmail))
        o.SenderEmail = builder.Configuration["Email:SenderEmail"] ?? string.Empty;
    if (string.IsNullOrWhiteSpace(o.SenderName))
        o.SenderName = builder.Configuration["Email:SenderName"] ?? "COMS";
});
builder.Services.AddHttpClient<IEmailService, BrevoEmailService>(client =>
{
    client.BaseAddress = new Uri("https://api.brevo.com/v3/");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddScoped<IOtpService, OtpService>();

// Photo authenticity (offline heuristic; runs after report save, never blocks it).
// Optional Railway/config shape:
//   ImageVerification__Enabled=true, ImageVerification__Provider=Heuristic
builder.Services.Configure<ImageVerificationOptions>(builder.Configuration.GetSection("ImageVerification"));
builder.Services.AddScoped<IImageVerificationService, HeuristicImageVerificationService>();

// Password-reset tokens expire after 1 hour (secure default).
builder.Services.Configure<DataProtectionTokenProviderOptions>(options =>
{
    options.TokenLifespan = TimeSpan.FromHours(1);
});

builder.Services.AddScoped<ISensorService, SensorService>();
builder.Services.AddScoped<IAlertService, AlertService>();
builder.Services.AddScoped<IFloodRiskService, FloodRiskService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IDataSeeder, DataSeeder>();

builder.Services.AddHostedService<SensorSimulationService>();

builder.Services.AddSignalR()
    .AddJsonProtocol();

builder.Services.AddControllersWithViews()
    .AddMvcOptions(options =>
    {
        options.Filters.Add<AutoValidateAntiforgeryTokenAttribute>();
    });

var app = builder.Build();

// Must run before any middleware that reads scheme/host (redirects, auth, links).
app.UseForwardedHeaders();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    db.Database.Migrate();

    var seeder = scope.ServiceProvider.GetRequiredService<IDataSeeder>();
    await seeder.SeedAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Dashboard/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
// TEMPORARY DIAGNOSTIC (remove after root-cause verdict — see report):
// Force gzip (never Brotli) for the Announcements HTML document only, by
// stripping "br" from what the compression middleware negotiates. All other
// pages and all static assets keep negotiating Brotli unchanged, and global
// compression stays ON. If /Announcements loads on Railway with this active,
// Brotli-at-the-edge is confirmed; if it still fails, Brotli is exonerated
// and this block must be deleted.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/Announcements", StringComparison.OrdinalIgnoreCase)
        && context.Request.Headers.AcceptEncoding.ToString()
            .Contains("gzip", StringComparison.OrdinalIgnoreCase))
    {
        context.Request.Headers.AcceptEncoding = "gzip";
    }
    await next();
});
// Must run before any middleware that serves responses (static files, MVC).
app.UseResponseCompression();

// Fingerprinted static assets (asp-append-version adds ?v=) are immutable:
// cache them for a year. Unversioned requests keep default ETag/304 behavior.
// Dynamic HTML, auth, and API responses are never affected (wwwroot only).
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        if (ctx.Context.Request.Query.ContainsKey("v"))
        {
            ctx.Context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
        }
    }
});
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.Use(async (context, next) =>
{
    await next();
    if (context.Response.StatusCode == 404 && !context.Response.HasStarted)
    {
        var path = context.Request.Path;
        // Friendly error page for PAGE navigations only. Asset/API/fetch
        // requests (images, fonts, JSON, ...) must keep their clean 404:
        // redirecting them to an HTML page serves an HTML (compressed) body
        // under an image/JSON URL, which poisons edge caches keyed by that
        // URL and surfaces in browsers as decoding/resource failures
        // (e.g. net::ERR_CONTENT_DECODING_FAILED on announcement images
        // whose uploaded file no longer exists on the host's ephemeral disk).
        var acceptsHtml = context.Request.Headers.Accept.ToString()
            .Contains("text/html", StringComparison.OrdinalIgnoreCase);
        var looksLikeFile = Path.GetExtension(path.Value ?? string.Empty).Length > 0;
        if (acceptsHtml && !looksLikeFile
            && !path.StartsWithSegments("/Dashboard/HttpError")
            && !path.StartsWithSegments("/Dashboard/Error")
            && !path.StartsWithSegments("/Dashboard/AccessDenied"))
        {
            context.Response.Redirect("/Dashboard/HttpError");
        }
    }
});

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}")
    .WithStaticAssets();

app.MapHub<MonitoringHub>("/monitoringhub");
app.MapHub<SupportHub>("/supporthub");

// Liveness probe for the host (Railway/Docker). No auth, no DB touch.
app.MapGet("/health", () => Results.Ok(new { status = "healthy", time = DateTime.UtcNow }));

app.Run();

public partial class Program { }
