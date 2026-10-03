using Dochub.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Dochub.Api.Data;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DochubDbContext>();
        var log = app.Logger;

        if (app.Configuration.GetValue("Database:AutoMigrate", true))
        {
            log.LogInformation("Applying database migrations…");
            await db.Database.MigrateAsync();
        }

        // There is no demo data: organizations are created by people. This only
        // makes sure the configured administrators can get in to create the first.
        await ApplyAdministratorsAsync(db, app.Configuration, log);
        await ApplyCreatorsAsync(db, app.Configuration, log);
    }

    /// <summary>
    /// Makes exactly the people listed in <c>Platform:Creators</c> Creators. Unlike
    /// administrators this is a sync, not an add-only grant: the role is powerful
    /// (it can make anyone the owner of any organization), so taking an address out
    /// of the list takes the role away on the next start.
    /// </summary>
    public static async Task ApplyCreatorsAsync(DochubDbContext db, IConfiguration config, ILogger log)
    {
        var emails = (config.GetSection("Platform:Creators").Get<string[]>() ?? [])
            .Select(e => e?.Trim().ToLowerInvariant())
            .Where(e => !string.IsNullOrWhiteSpace(e) && e.Contains('@'))
            .Select(e => e!)
            .ToHashSet();

        foreach (var revoked in await db.Users.Where(u => u.IsCreator && !emails.Contains(u.Email)).ToListAsync())
        {
            revoked.IsCreator = false;
            log.LogWarning("{Email} is no longer a Creator (not in Platform:Creators)", revoked.Email);
        }

        foreach (var email in emails)
        {
            var user = await db.Users.FirstOrDefaultAsync(x => x.Email == email);
            if (user is null)
            {
                user = new User
                {
                    Email = email, DisplayName = DeriveDisplayName(email),
                    IdentityProvider = "invited", ExternalSubject = $"invite:{Guid.NewGuid():n}"
                };
                db.Users.Add(user);
            }
            user.IsCreator = true;
            user.CanCreateOrganizations = true;
            log.LogInformation("{Email} is a Creator", email);
        }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Ensures everyone listed in <c>Database:Administrators</c> can sign in and
    /// create organizations, and is an Admin of the organizations that exist.
    ///
    /// This runs every start rather than only on a fresh database, so the grant
    /// survives a <c>docker compose down -v</c> and does not need re-applying by
    /// hand. It only ever adds access; it never demotes or removes anyone.
    /// </summary>
    private static async Task ApplyAdministratorsAsync(DochubDbContext db, IConfiguration config, ILogger log)
    {
        var emails = config.GetSection("Database:Administrators").Get<string[]>() ?? [];
        if (emails.Length == 0) return;

        foreach (var raw in emails)
        {
            var email = raw?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            {
                log.LogWarning("Skipping '{Entry}' in Database:Administrators — not an email address", raw);
                continue;
            }

            var user = await db.Users.FirstOrDefaultAsync(x => x.Email == email);
            if (user is null)
            {
                // A placeholder, exactly as an owner's invite creates: it binds to
                // a real provider subject the first time they sign in.
                user = new User
                {
                    Email = email,
                    DisplayName = DeriveDisplayName(email),
                    IdentityProvider = "invited",
                    ExternalSubject = $"invite:{Guid.NewGuid():n}"
                };
                db.Users.Add(user);
                log.LogInformation("Created {Email} from Database:Administrators", email);
            }

            user.CanCreateOrganizations = true;
            await db.SaveChangesAsync();

            var organizations = await db.Organizations.OrderBy(x => x.CreatedAt).ToListAsync();
            foreach (var organization in organizations)
            {
                var membership = await db.OrganizationMembers
                    .FirstOrDefaultAsync(x => x.OrganizationId == organization.Id && x.UserId == user.Id);

                if (membership is null)
                {
                    db.OrganizationMembers.Add(new OrganizationMember
                    {
                        OrganizationId = organization.Id,
                        UserId = user.Id,
                        Role = OrgRole.Admin,
                        CanCreateOrganizations = true
                    });
                }
                else
                {
                    // Never demote: an Owner listed here stays an Owner.
                    if (membership.Role < OrgRole.Admin) membership.Role = OrgRole.Admin;
                    membership.CanCreateOrganizations = true;
                }
            }

            await db.SaveChangesAsync();
            log.LogInformation("{Email} is an administrator and can create organizations ({Count} organization(s))",
                email, organizations.Count);
        }
    }

    private static string DeriveDisplayName(string email) =>
        string.Join(' ', email.Split('@')[0].Split('.', '_', '-')
            .Where(x => x.Length > 0)
            .Select(x => char.ToUpperInvariant(x[0]) + x[1..]));
}
