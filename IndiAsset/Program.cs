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

// Register MongoDbService
builder.Services.AddSingleton<MongoDbService>();

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
        mongo.ConnectionString =
            $"{mongoConnectionString}/{mongoDatabaseName}";
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
// BUILD APP
// ======================================================

var app = builder.Build();

// Seed Roles
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
}

// ======================================================
// MIDDLEWARE
// ======================================================

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
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