using AspNetCoreIdentity.MongoDriver.Models;
using Microsoft.AspNetCore.Identity;

namespace IndiAsset.Data
{
    public static class RoleSeeder
    {
        public static async Task SeedRolesAsync(
            IServiceProvider serviceProvider)
        {
            var roleManager =
                serviceProvider.GetRequiredService<RoleManager<MongoRole<string>>>();

            string[] roles =
            {
                "Admin",
                "User"
            };

            foreach (var role in roles)
            {
                if (!await roleManager.RoleExistsAsync(role))
                {
                    await roleManager.CreateAsync(
                        new MongoRole<string> { Name = role });
                }
            }
        }
    }
}