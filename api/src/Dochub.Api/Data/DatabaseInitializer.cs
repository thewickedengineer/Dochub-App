using Dochub.Api.Domain;
using Dochub.Api.Endpoints;
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

        if (!app.Configuration.GetValue("Database:Seed", false)) return;
        if (await db.Organizations.AnyAsync())
        {
            log.LogInformation("Seed skipped — the database already has an organization.");
            return;
        }

        log.LogInformation("Seeding the demo organization…");
        await SeedAsync(db, app.Configuration["Database:SeedOwnerEmail"] ?? "owner@acme-insurance.com");
        log.LogInformation("Seed complete.");
    }

    /// <summary>
    /// Builds the structure the UI design shows (Claims / Policy / Underwriting)
    /// so a fresh database is immediately explorable. No documents are seeded —
    /// those only exist once a real source has been uploaded.
    /// </summary>
    private static async Task SeedAsync(DochubDbContext db, string ownerEmail)
    {
        var owner = new User
        {
            Email = ownerEmail.ToLowerInvariant(),
            DisplayName = "Dana Whitfield",
            IdentityProvider = "invited",
            ExternalSubject = $"invite:{Guid.NewGuid():n}",
            CanCreateOrganizations = true
        };

        var colleagues = new[]
        {
            new User { Email = "priya@acme-insurance.com", DisplayName = "Priya Shah", IdentityProvider = "invited", ExternalSubject = $"invite:{Guid.NewGuid():n}" },
            new User { Email = "marcus@acme-insurance.com", DisplayName = "Marcus Lee", IdentityProvider = "invited", ExternalSubject = $"invite:{Guid.NewGuid():n}" },
            new User { Email = "elena@acme-insurance.com", DisplayName = "Elena Ruiz", IdentityProvider = "invited", ExternalSubject = $"invite:{Guid.NewGuid():n}" }
        };

        db.Users.Add(owner);
        db.Users.AddRange(colleagues);

        var organization = new Organization
        {
            Name = "Acme Insurance",
            Slug = "acme-insurance",
            Initials = "AI",
            Plan = "Enterprise",
            CreatedByUserId = owner.Id
        };
        db.Organizations.Add(organization);

        db.OrganizationMembers.Add(new OrganizationMember
        {
            Organization = organization, User = owner, Role = OrgRole.Owner, CanCreateOrganizations = true
        });
        db.OrganizationMembers.Add(new OrganizationMember
        {
            Organization = organization, User = colleagues[0], Role = OrgRole.Admin, CanCreateOrganizations = true
        });
        db.OrganizationMembers.Add(new OrganizationMember { Organization = organization, User = colleagues[1], Role = OrgRole.Member });
        db.OrganizationMembers.Add(new OrganizationMember { Organization = organization, User = colleagues[2], Role = OrgRole.Member });

        var structure = new (string Team, (string Group, (string Artifact, string Category, SourceType Source)[] Artifacts)[] Groups)[]
        {
            ("Claims", [
                ("Claims IT", [
                    ("Claims Core Repo", "Source code", SourceType.GitHub),
                    ("Claims IT Technical", "Technical documentation", SourceType.SharePoint)
                ]),
                ("Claims Business", [
                    ("Business Workflow", "Business workflow", SourceType.SharePoint),
                    ("Adjuster Playbooks", "Operating procedures", SourceType.GoogleDrive)
                ])
            ]),
            ("Policy", [
                ("Policy Admin", [
                    ("Policy Forms Library", "Policy documents", SourceType.GoogleDrive),
                    ("Rating Engine", "Source code", SourceType.GitHub)
                ])
            ]),
            ("Underwriting", [
                ("Guidelines", [
                    ("UW Manuals", "Policy documents", SourceType.Local)
                ])
            ])
        };

        foreach (var (teamName, groups) in structure)
        {
            var team = new Team
            {
                Organization = organization,
                Name = teamName,
                Slug = Mapping.Slugify(teamName),
                CreatedByUserId = owner.Id
            };
            db.Teams.Add(team);
            db.TeamMembers.Add(new TeamMember { Team = team, User = owner, Role = TeamRole.Lead });

            foreach (var (groupName, artifacts) in groups)
            {
                var group = new Group
                {
                    Team = team,
                    Name = groupName,
                    Slug = Mapping.Slugify(groupName),
                    CreatedByUserId = owner.Id
                };
                db.Groups.Add(group);

                foreach (var (artifactName, category, source) in artifacts)
                {
                    db.Artifacts.Add(new Artifact
                    {
                        Group = group,
                        Name = artifactName,
                        Slug = Mapping.Slugify(artifactName),
                        Category = category,
                        PrimarySource = source,
                        CreatedByUserId = owner.Id
                    });
                }
            }
        }

        await db.SaveChangesAsync();
    }
}
