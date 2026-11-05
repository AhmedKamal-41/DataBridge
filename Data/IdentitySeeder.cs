using DataBridge.Models;
using Microsoft.AspNetCore.Identity;

namespace DataBridge.Data;

// Creates the roles and, when seedUsers is true, the local users listed in configuration.
public static class IdentitySeeder
{
    public static async Task SeedAsync(IServiceProvider services, IConfiguration configuration, bool seedUsers)
    {
        using IServiceScope scope = services.CreateScope();
        RoleManager<IdentityRole> roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        UserManager<IdentityUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();

        foreach (string role in AppRoles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                CheckResult(await roleManager.CreateAsync(new IdentityRole(role)), $"role {role}");
            }
        }

        if (!seedUsers)
        {
            return;
        }

        foreach (IConfigurationSection section in configuration.GetSection("Seed:Users").GetChildren())
        {
            string email = section["Email"] ?? throw new InvalidOperationException($"{section.Path}:Email is missing.");

            // An existing user is left alone, so changing a secret can't reset a password,
            // and the bootstrap password can be removed once the account exists.
            if (await userManager.FindByEmailAsync(email) != null)
            {
                continue;
            }

            string password = section["Password"] ?? throw new InvalidOperationException($"{section.Path}:Password is missing.");
            string role = section["Role"] ?? throw new InvalidOperationException($"{section.Path}:Role is missing.");

            if (!AppRoles.All.Contains(role))
            {
                throw new InvalidOperationException($"{section.Path}:Role must be one of: {string.Join(", ", AppRoles.All)}.");
            }

            var user = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true };
            CheckResult(await userManager.CreateAsync(user, password), $"user {email}");
            CheckResult(await userManager.AddToRoleAsync(user, role), $"role for {email}");
        }
    }

    private static void CheckResult(IdentityResult result, string description)
    {
        if (!result.Succeeded)
        {
            string errors = string.Join(" ", result.Errors.Select(error => error.Description));
            throw new InvalidOperationException($"Could not create {description}: {errors}");
        }
    }
}
