using IndiAsset.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using IndiAsset.Models;
using IndiAsset.Services;
using MongoDB.Bson;
using MongoDB.Driver;


var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(connectionString));

builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddSingleton<MongoDbService>();

builder.Services.AddDefaultIdentity<ApplicationUser>(options =>
options.SignIn.RequireConfirmedAccount = false)
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>();

builder.Services.AddControllersWithViews();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var roleManager =
        scope.ServiceProvider
            .GetRequiredService<RoleManager<IdentityRole>>();

    await RoleSeeder.SeedRolesAsync(roleManager);
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

app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.MapRazorPages()
   .WithStaticAssets();

try
{
    var mongoConnectionString = builder.Configuration
        .GetSection("MongoDB")["ConnectionString"]
        ?? throw new InvalidOperationException(
            "MongoDB ConnectionString is missing");

    var mongoDatabaseName = builder.Configuration
        .GetSection("MongoDB")["DatabaseName"]
        ?? throw new InvalidOperationException(
            "MongoDB DatabaseName is missing");

    var mongoClient = new MongoClient(mongoConnectionString);

    await mongoClient
        .GetDatabase(mongoDatabaseName)
        .RunCommandAsync<BsonDocument>(
            new BsonDocument("ping", 1));

    Console.WriteLine("MongoDB connected successfully!");
}
catch (Exception ex)
{
    Console.WriteLine("MongoDB connection failed: " + ex.Message);
}

app.Run();
