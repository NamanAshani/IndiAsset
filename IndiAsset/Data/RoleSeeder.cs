using Microsoft.AspNetCore.Identity;

namespace IndiAsset.Data
{
    public class RoleSeeder
    {
        public static async Task SeedRolesAsync(
           RoleManager<IdentityRole> roleManager)
        {
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
                        new IdentityRole(role));
                }
            }
        }
    }
}

