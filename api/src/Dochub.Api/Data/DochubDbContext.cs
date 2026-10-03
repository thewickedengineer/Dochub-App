using Dochub.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Dochub.Api.Data;

public class DochubDbContext(DbContextOptions<DochubDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationMember> OrganizationMembers => Set<OrganizationMember>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<Artifact> Artifacts => Set<Artifact>();
    public DbSet<SourceConnection> SourceConnections => Set<SourceConnection>();
    public DbSet<SourceDocument> SourceDocuments => Set<SourceDocument>();
    public DbSet<UploadedDocument> UploadedDocuments => Set<UploadedDocument>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<QueueMessage> QueueMessages => Set<QueueMessage>();
    public DbSet<DocumentVersion> DocumentVersions => Set<DocumentVersion>();
    public DbSet<RecurringSyncSchedule> RecurringSyncSchedules => Set<RecurringSyncSchedule>();
    public DbSet<ChatConversation> ChatConversations => Set<ChatConversation>();
    public DbSet<NotificationDismissal> NotificationDismissals => Set<NotificationDismissal>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("dochub");
        // Source references (SRC-1042) come from a sequence so concurrent Process clicks can't collide.
        b.HasSequence<long>("source_document_number", "dochub").StartsAt(1000).IncrementsBy(1);

        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.Property(x => x.Email).HasMaxLength(320).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            e.Property(x => x.IdentityProvider).HasMaxLength(32).IsRequired();
            e.Property(x => x.ExternalSubject).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => new { x.IdentityProvider, x.ExternalSubject }).IsUnique();
        });

        b.Entity<Organization>(e =>
        {
            e.ToTable("organizations");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(200).IsRequired();
            e.Property(x => x.Initials).HasMaxLength(4).IsRequired();
            e.Property(x => x.Plan).HasMaxLength(50).IsRequired();
            e.HasIndex(x => x.Slug).IsUnique();
        });

        b.Entity<OrganizationMember>(e =>
        {
            e.ToTable("organization_members");
            e.HasIndex(x => new { x.OrganizationId, x.UserId }).IsUnique();
            e.HasOne(x => x.Organization).WithMany(x => x.Members)
                .HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany(x => x.Memberships)
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Team>(e =>
        {
            e.ToTable("teams");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(200).IsRequired();
            e.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.Slug }).IsUnique();
            e.HasOne(x => x.Organization).WithMany(x => x.Teams)
                .HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TeamMember>(e =>
        {
            e.ToTable("team_members");
            e.HasIndex(x => new { x.TeamId, x.UserId }).IsUnique();
            e.HasOne(x => x.Team).WithMany(x => x.Members)
                .HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany()
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Group>(e =>
        {
            e.ToTable("groups");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(200).IsRequired();
            e.HasIndex(x => new { x.TeamId, x.Name }).IsUnique();
            e.HasOne(x => x.Team).WithMany(x => x.Groups)
                .HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Artifact>(e =>
        {
            e.ToTable("artifacts");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(200).IsRequired();
            e.Property(x => x.Category).HasMaxLength(100).IsRequired();
            e.HasIndex(x => new { x.GroupId, x.Name }).IsUnique();
            e.HasOne(x => x.Group).WithMany(x => x.Artifacts)
                .HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SourceConnection>(e =>
        {
            e.ToTable("source_connections");
            e.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Account).HasMaxLength(320);
            e.Property(x => x.Metadata).HasColumnType("jsonb");
            e.HasIndex(x => new { x.OrganizationId, x.UserId, x.SourceType }).IsUnique();
            e.HasOne(x => x.Organization).WithMany()
                .HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SourceDocument>(e =>
        {
            e.ToTable("source_documents");
            e.Property(x => x.Reference).HasMaxLength(50).IsRequired();
            e.Property(x => x.SourceReference).HasMaxLength(1000).IsRequired();
            e.Property(x => x.SourceOptions).HasColumnType("jsonb");
            e.Property(x => x.BlobContainer).HasMaxLength(200);
            e.Property(x => x.BlobPrefix).HasMaxLength(1000);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.HasIndex(x => x.Reference).IsUnique();
            e.HasIndex(x => new { x.ArtifactId, x.RequestedAt });
            e.HasIndex(x => new { x.OrganizationId, x.RequestedAt });
            // The processing list and the recovery sweep both filter on in-flight work.
            e.HasIndex(x => x.Status).HasFilter("status < 4");
            e.HasIndex(x => new { x.SyncScheduleId, x.RequestedAt }).HasFilter("sync_schedule_id IS NOT NULL");
            e.HasOne(x => x.Artifact).WithMany(x => x.SourceDocuments)
                .HasForeignKey(x => x.ArtifactId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.RequestedByUser).WithMany()
                .HasForeignKey(x => x.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<UploadedDocument>(e =>
        {
            e.ToTable("uploaded_documents");
            e.Property(x => x.Name).HasMaxLength(500).IsRequired();
            e.Property(x => x.RelativePath).HasMaxLength(1000).IsRequired();
            e.Property(x => x.SourceLocation).HasMaxLength(1000).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(200);
            e.Property(x => x.ChecksumSha256).HasMaxLength(64);
            e.Property(x => x.BlobPath).HasMaxLength(1200);
            e.Property(x => x.BlobUrl).HasMaxLength(2000);
            e.Property(x => x.ContentMd5).HasMaxLength(64);
            e.Property(x => x.BlobETag).HasMaxLength(100);
            e.Property(x => x.ExternalId).HasMaxLength(500);
            // Every document starts at revision 1; a sync that replaces its
            // content bumps it. Defaulted in the database so rows created by
            // raw SQL or a backfill land on the same baseline.
            e.Property(x => x.Revision).HasDefaultValue(1);
            e.HasIndex(x => new { x.ArtifactId, x.Status });
            e.HasIndex(x => x.SourceDocumentId);
            // Re-importing the same source item updates the row instead of duplicating it.
            e.HasIndex(x => new { x.ArtifactId, x.SourceType, x.ExternalId })
                .IsUnique().HasFilter("external_id IS NOT NULL");
            e.HasOne(x => x.SourceDocument).WithMany(x => x.Documents)
                .HasForeignKey(x => x.SourceDocumentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Artifact).WithMany(x => x.Documents)
                .HasForeignKey(x => x.ArtifactId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RecurringSyncSchedule>(e =>
        {
            e.ToTable("recurring_sync_schedules");
            e.Property(x => x.SourceReference).HasMaxLength(1000).IsRequired();
            e.Property(x => x.SourceOptions).HasColumnType("jsonb");
            e.Property(x => x.TimeZoneId).HasMaxLength(100).IsRequired();
            e.Property(x => x.LastError).HasMaxLength(2000);
            // One standing schedule per artifact+source: a second cadence for the
            // same location would just fight the first one.
            e.HasIndex(x => new { x.ArtifactId, x.SourceType }).IsUnique();
            // The worker's only hot query is "what is due now".
            e.HasIndex(x => new { x.Status, x.NextRunAt }).HasFilter("status = 0");
            e.HasOne(x => x.Artifact).WithMany()
                .HasForeignKey(x => x.ArtifactId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.CreatedByUser).WithMany()
                .HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<DocumentVersion>(e =>
        {
            e.ToTable("document_versions");
            e.Property(x => x.Name).HasMaxLength(500).IsRequired();
            e.Property(x => x.RelativePath).HasMaxLength(1000).IsRequired();
            e.Property(x => x.SourceLocation).HasMaxLength(1000).IsRequired();
            e.Property(x => x.ExternalId).HasMaxLength(500);
            e.Property(x => x.ContentType).HasMaxLength(200);
            e.Property(x => x.BlobContainer).HasMaxLength(200).IsRequired();
            e.Property(x => x.BlobPath).HasMaxLength(1200).IsRequired();
            e.Property(x => x.BlobUrl).HasMaxLength(2000).IsRequired();
            e.Property(x => x.BlobETag).HasMaxLength(100);
            e.Property(x => x.ContentMd5).HasMaxLength(64).IsRequired();
            e.Property(x => x.ContentSha256).HasMaxLength(64).IsRequired();
            e.HasIndex(x => new { x.UploadedDocumentId, x.Revision }).IsUnique();
            // "Have we seen these bytes for this name before?" — the sync's question.
            e.HasIndex(x => new { x.ArtifactId, x.Name, x.UploadedAt });
            e.HasIndex(x => x.ContentMd5);
            e.HasOne(x => x.UploadedDocument).WithMany()
                .HasForeignKey(x => x.UploadedDocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<QueueMessage>(e =>
        {
            e.ToTable("queue_messages");
            e.Property(x => x.Queue).HasMaxLength(200).IsRequired();
            e.Property(x => x.MessageId).HasMaxLength(100).IsRequired();
            e.Property(x => x.Subject).HasMaxLength(200).IsRequired();
            e.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.SessionId).HasMaxLength(128);
            e.Property(x => x.Error).HasMaxLength(2000);
            // Broker-style dedupe: the outbox may hand us the same id twice.
            e.HasIndex(x => new { x.Queue, x.MessageId }).IsUnique();
            // The receive query reads exactly this: pending rows on one queue, oldest first.
            e.HasIndex(x => new { x.Queue, x.Status, x.Id }).HasFilter("status IN (0, 1)");
        });

        b.Entity<Notification>(e =>
        {
            e.ToTable("notifications");
            e.Property(x => x.Type).HasMaxLength(100).IsRequired();
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Body).HasMaxLength(2000).IsRequired();
            e.Property(x => x.EntityType).HasMaxLength(50);
            e.HasIndex(x => new { x.OrganizationId, x.UserId, x.CreatedAt });
        });

        b.Entity<NotificationDismissal>(e =>
        {
            e.ToTable("notification_dismissals");
            e.HasKey(x => new { x.UserId, x.NotificationId });
            e.HasOne<Notification>().WithMany().HasForeignKey(x => x.NotificationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ChatConversation>(e =>
        {
            e.ToTable("chat_conversations");
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.ScopeLabel).HasMaxLength(300).IsRequired();
            // The conversation list: one person's, in one org, most recent first.
            e.HasIndex(x => new { x.OrganizationId, x.UserId, x.UpdatedAt });
            e.HasMany(x => x.Messages).WithOne(x => x.Conversation)
                .HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ChatMessage>(e =>
        {
            e.ToTable("chat_messages");
            e.Property(x => x.Content).IsRequired();
            e.Property(x => x.Sources).HasColumnType("jsonb");
            e.Property(x => x.Cited).HasColumnType("jsonb");
            e.Property(x => x.Usage).HasColumnType("jsonb");
            e.Property(x => x.Model).HasMaxLength(200);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.HasIndex(x => new { x.ConversationId, x.CreatedAt });
        });

        // snake_case every column so the schema reads naturally from psql.
        foreach (var entity in b.Model.GetEntityTypes())
            foreach (var prop in entity.GetProperties())
                prop.SetColumnName(ToSnakeCase(prop.GetColumnName()));

        // "ETag" would otherwise become e_tag.
        b.Entity<UploadedDocument>().Property(x => x.BlobETag).HasColumnName("blob_etag");
        b.Entity<DocumentVersion>().Property(x => x.BlobETag).HasColumnName("blob_etag");
    }

    private static string ToSnakeCase(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && !char.IsUpper(name[i + 1]))))
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
