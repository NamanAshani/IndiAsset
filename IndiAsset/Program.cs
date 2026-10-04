using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AspNetCoreIdentity.MongoDriver;
using AspNetCoreIdentity.MongoDriver.Models;
using IndiAsset.Data;
using IndiAsset.Hubs;
using IndiAsset.Models;
using IndiAsset.Services;
using Microsoft.AspNetCore.Identity.UI.Services;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

// ======================================================
// LOAD .ENV ENVIRONMENT VARIABLES
// ======================================================
var envCandidates = new[]
{
    Path.Combine(builder.Environment.ContentRootPath, ".env"),
    Path.Combine(Directory.GetCurrentDirectory(), ".env"),
    Path.Combine(Directory.GetCurrentDirectory(), "IndiAsset", ".env")
};

foreach (var envPath in envCandidates)
{
    if (File.Exists(envPath))
    {
        foreach (var line in File.ReadAllLines(envPath))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#")) continue;
            var eqIdx = trimmed.IndexOf('=');
            if (eqIdx > 0)
            {
                var key = trimmed.Substring(0, eqIdx).Trim();
                var val = trimmed.Substring(eqIdx + 1).Trim().Trim('"', '\'');
                Environment.SetEnvironmentVariable(key, val);
                builder.Configuration[key] = val;

                if (key.Equals("RAZORPAY_KEY_ID", StringComparison.OrdinalIgnoreCase))
                {
                    builder.Configuration["Razorpay:KeyId"] = val;
                }
                else if (key.Equals("RAZORPAY_KEY_SECRET", StringComparison.OrdinalIgnoreCase))
                {
                    builder.Configuration["Razorpay:KeySecret"] = val;
                }
                else if (key.Equals("MONGODB_CONNECTION_STRING", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("MONGODB_URI", StringComparison.OrdinalIgnoreCase))
                {
                    builder.Configuration["MongoDB:ConnectionString"] = val;
                }
                else if (key.Equals("MONGODB_DATABASE_NAME", StringComparison.OrdinalIgnoreCase))
                {
                    builder.Configuration["MongoDB:DatabaseName"] = val;
                }
            }
        }
        break;
    }
}

// ======================================================
// MONGODB CONNECTION
// ======================================================

var mongoConnectionString =
    builder.Configuration["MongoDB:ConnectionString"]
    ?? throw new InvalidOperationException(
        "MongoDB ConnectionString is missing.");

var mongoDatabaseName =
    builder.Configuration["MongoDB:DatabaseName"]
    ?? throw new InvalidOperationException(
        "MongoDB DatabaseName is missing.");

// Register MongoDbService & GridFsService
builder.Services.AddSingleton<MongoDbService>();
builder.Services.AddSingleton<GridFsService>();
builder.Services.AddSingleton<PresenceTracker>();
builder.Services.AddScoped<AssetAvailabilityService>();
builder.Services.AddScoped<IRazorpayService, RazorpayService>();
builder.Services.AddHostedService<MongoChangeStreamService>();

// Register EmailSender
builder.Services.AddTransient<IEmailSender, EmailSender>();

// ======================================================
// ASP.NET CORE IDENTITY + MONGODB
// ======================================================

builder.Services.AddIdentityMongoDbProvider<
    ApplicationUser,
    MongoRole<string>,
    string
>(
    identity =>
    {
        identity.User.RequireUniqueEmail = true;
        identity.SignIn.RequireConfirmedAccount = false;
        identity.Password.RequireDigit = false;
        identity.Password.RequiredLength = 6;
        identity.Password.RequireNonAlphanumeric = false;
        identity.Password.RequireUppercase = false;
        identity.Password.RequireLowercase = false;
    },
    mongo =>
    {
        var urlBuilder = new MongoUrlBuilder(mongoConnectionString)
        {
            DatabaseName = mongoDatabaseName
        };
        mongo.ConnectionString = urlBuilder.ToMongoUrl().Url;
    }
);

// Configure Application Cookie
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Identity/Account/Login";
    options.LogoutPath = "/Identity/Account/Logout";
    options.AccessDeniedPath = "/Identity/Account/AccessDenied";
});

// ======================================================
// MVC, RAZOR PAGES & SIGNALR
// ======================================================

builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();
builder.Services.AddSignalR();

// ======================================================
// PORT FALLBACK CONFIGURATION (DYNAMIC PORT SELECTION)
// ======================================================
ConfigureAvailablePorts(builder);

// ======================================================
// BUILD APP
// ======================================================

var app = builder.Build();

// Seed Roles and Migrate Local Uploads to GridFS
using (var scope = app.Services.CreateScope())
{
    try
    {
        await RoleSeeder.SeedRolesAsync(scope.ServiceProvider);
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogWarning(ex, "Could not seed roles on startup: {Message}", ex.Message);
    }

    try
    {
        var gridFs = scope.ServiceProvider.GetRequiredService<GridFsService>();
        var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        await gridFs.MigrateExistingLocalUploadsAsync(env);
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogWarning(ex, "Could not run GridFS upload migration on startup: {Message}", ex.Message);
    }
}

// ======================================================
// MIDDLEWARE
// ======================================================

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// ======================================================
// ROUTING & HUBS
// ======================================================

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapRazorPages();
app.MapHub<ChatHub>("/chatHub");


app.Run();

// ======================================================
// PORT FALLBACK HELPER METHODS
// ======================================================

static void ConfigureAvailablePorts(WebApplicationBuilder builder)
{
    var configuredUrls = builder.Configuration["urls"] 
        ?? builder.Configuration["ASPNETCORE_URLS"] 
        ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS");

    if (string.IsNullOrWhiteSpace(configuredUrls))
    {
        configuredUrls = "http://localhost:5231";
    }

    var urlEntries = configuredUrls.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
    var resolvedUrls = new List<string>();
    bool hasChanged = false;

    foreach (var entry in urlEntries)
    {
        var trimmed = entry.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            var port = uri.Port;
            if (port > 0 && IsPortInUse(port))
            {
                var nextPort = GetAvailablePort(port + 1);
                var adjustedUrl = $"{uri.Scheme}://{uri.Host}:{nextPort}";
                resolvedUrls.Add(adjustedUrl);
                hasChanged = true;
                Console.WriteLine($"[IndiAsset] Notice: Port {port} is already in use. Automatically switched to available port {nextPort} ({adjustedUrl})");
            }
            else
            {
                resolvedUrls.Add(trimmed);
            }
        }
        else
        {
            resolvedUrls.Add(trimmed);
        }
    }

    if (hasChanged || resolvedUrls.Count > 0)
    {
        var joined = string.Join(";", resolvedUrls);
        builder.WebHost.UseUrls(resolvedUrls.ToArray());
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", joined);
        builder.Configuration["urls"] = joined;
        builder.Configuration["ASPNETCORE_URLS"] = joined;
    }
}

static bool IsPortInUse(int port)
{
    try
    {
        var ipGlobal = IPGlobalProperties.GetIPGlobalProperties();
        var tcpListeners = ipGlobal.GetActiveTcpListeners();
        if (tcpListeners.Any(ep => ep.Port == port)) return true;

        var tcpConnections = ipGlobal.GetActiveTcpConnections();
        if (tcpConnections.Any(conn => conn.LocalEndPoint.Port == port)) return true;
    }
    catch { }

    try
    {
        using var tcp1 = new TcpListener(IPAddress.Loopback, port);
        tcp1.Start();
        tcp1.Stop();
    }
    catch
    {
        return true;
    }

    try
    {
        using var tcp2 = new TcpListener(IPAddress.Any, port);
        tcp2.Start();
        tcp2.Stop();
    }
    catch
    {
        return true;
    }

    return false;
}

static int GetAvailablePort(int startPort)
{
    for (int port = startPort; port < startPort + 500; port++)
    {
        if (!IsPortInUse(port))
        {
            return port;
        }
    }

    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    return ((IPEndPoint)socket.LocalEndPoint!).Port;
}