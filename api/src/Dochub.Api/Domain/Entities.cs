using System.Text.Json;
using Dochub.Api.Endpoints;

namespace Dochub.Api.Domain;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public string? AvatarUrl { get; set; }
    /// <summary>"google" or "microsoft" — the SSO provider that created/last authenticated this user.</summary>
    public string IdentityProvider { get; set; } = default!;
    /// <summary>Stable subject claim from the provider. Unique per provider.</summary>
    public string ExternalSubject { get; set; } = default!;
    /// <summary>Platform-level flag gating "Create organization". Org Owners/Admins get it implicitly.</summary>
    public bool CanCreateOrganizations { get; set; }
    /// <summary>
    /// Platform-level Creator: creates organizations and assigns or replaces their
    /// owners, without having to be a member of them. Granted only from
    /// configuration (Platform:Creators), never through the API.
    /// </summary>
    public bool IsCreator { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }

    public ICollection<OrganizationMember> Memberships { get; set; } = [];
}

public class Organization
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = default!;
    public string Slug { get; set; } = default!;
    public string Initials { get; set; } = default!;
    public string Plan { get; set; } = "Trial";
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<OrganizationMember> Members { get; set; } = [];
    public ICollection<Team> Teams { get; set; } = [];
}

public class OrganizationMember
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = default!;
    public Guid UserId { get; set; }
    public User User { get; set; } = default!;
    public OrgRole Role { get; set; } = OrgRole.Member;
    /// <summary>Per-org override of <see cref="User.CanCreateOrganizations"/>.</summary>
    public bool CanCreateOrganizations { get; set; }
    public Guid? InvitedByUserId { get; set; }
    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Team
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = default!;
    public string Name { get; set; } = default!;
    /// <summary>URL/blob-path-safe form of <see cref="Name"/>. Used in the blob prefix.</summary>
    public string Slug { get; set; } = default!;
    public string? Description { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<Group> Groups { get; set; } = [];
    public ICollection<TeamMember> Members { get; set; } = [];
}

public class TeamMember
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TeamId { get; set; }
    public Team Team { get; set; } = default!;
    public Guid UserId { get; set; }
    public User User { get; set; } = default!;
    public TeamRole Role { get; set; } = TeamRole.Member;
    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Group
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TeamId { get; set; }
    public Team Team { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Slug { get; set; } = default!;
    public string? Description { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<Artifact> Artifacts { get; set; } = [];
}

/// <summary>A category bucket of documents inside a group, e.g. "Claims IT Technical".</summary>
public class Artifact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GroupId { get; set; }
    public Group Group { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Slug { get; set; } = default!;
    public string Category { get; set; } = default!;
    public SourceType PrimarySource { get; set; }
    public string? Description { get; set; }
    /// <summary>Set by the vector service's completion call, not inferred.</summary>
    public ArtifactStatus Status { get; set; } = ArtifactStatus.Empty;
    public DateTimeOffset? LastProcessedAt { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<SourceDocument> SourceDocuments { get; set; } = [];
    public ICollection<UploadedDocument> Documents { get; set; } = [];
}

/// <summary>An OAuth/token grant for a source system, scoped to one org and the user who granted it.</summary>
public class SourceConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = default!;
    public Guid UserId { get; set; }
    public SourceType SourceType { get; set; }
    public string DisplayName { get; set; } = default!;
    public ConnectionStatus Status { get; set; } = ConnectionStatus.Connected;
    /// <summary>Protected with ASP.NET Data Protection — never stored or returned in the clear.</summary>
    public string? ProtectedAccessToken { get; set; }
    public string? ProtectedRefreshToken { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? Scopes { get; set; }
    /// <summary>Which account consented, shown so people can tell two connections apart.</summary>
    public string? Account { get; set; }
    public JsonDocument? Metadata { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A source submitted for processing — the row written when Process is pressed.
/// Nothing reaches the backend before that: the browser holds the chosen repo,
/// folder or files until the user commits to them.
///
/// This row is the unit of work the whole pipeline moves through, so its status
/// is the single answer to "where has my upload got to".
/// </summary>
public class SourceDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Display id, e.g. SRC-1042. Sequenced so concurrent submissions never collide.</summary>
    public string Reference { get; set; } = default!;
    public Guid OrganizationId { get; set; }
    public Guid ArtifactId { get; set; }
    public Artifact Artifact { get; set; } = default!;

    public SourceType SourceType { get; set; }
    public Guid? SourceConnectionId { get; set; }
    /// <summary>Human-readable pointer, e.g. "acme-ins/claims-core@main:/docs".</summary>
    public string SourceReference { get; set; } = default!;
    /// <summary>Connector payload the extractor needs: repo/branch/path, drive ids, staging ids.</summary>
    public JsonDocument? SourceOptions { get; set; }

    public Guid RequestedByUserId { get; set; }
    public User RequestedByUser { get; set; } = default!;
    /// <summary>Set when a recurring schedule submitted this rather than a person.</summary>
    public Guid? SyncScheduleId { get; set; }

    public SourceDocumentStatus Status { get; set; } = SourceDocumentStatus.RequestUpload;
    public int TotalDocuments { get; set; }
    public int UploadedDocumentCount { get; set; }
    public int ProcessedDocumentCount { get; set; }
    public int FailedDocumentCount { get; set; }
    /// <summary>Reconciliation tallies, meaningful when this source came from a schedule.</summary>
    public int AddedDocumentCount { get; set; }
    public int UpdatedDocumentCount { get; set; }
    public int UnchangedDocumentCount { get; set; }
    /// <summary>Documents a sync found gone from the source, and removed from Dochub and the index.</summary>
    public int RemovedDocumentCount { get; set; }

    public string? BlobContainer { get; set; }
    /// <summary>teams/{team}/groups/{group}/artifacts/{artifact}/{yyyyMMddTHHmmssfffZ}</summary>
    public string? BlobPrefix { get; set; }

    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UploadStartedAt { get; set; }
    public DateTimeOffset? UploadedAt { get; set; }
    public DateTimeOffset? ProcessingStartedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public string? Error { get; set; }
    /// <summary>How many times the extractor has picked this up, for poison-message handling.</summary>
    public int DeliveryCount { get; set; }

    public ICollection<UploadedDocument> Documents { get; set; } = [];
}

/// <summary>Status row for one document within an uploaded source.</summary>
public class UploadedDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceDocumentId { get; set; }
    public SourceDocument SourceDocument { get; set; } = default!;
    /// <summary>Denormalized from the source so artifact-scoped reads stay a single-table scan.</summary>
    public Guid ArtifactId { get; set; }
    public Artifact Artifact { get; set; } = default!;
    public SourceType SourceType { get; set; }
    /// <summary>Id in the source system (git blob sha, Graph driveItem id, ...) for idempotent re-imports.</summary>
    public string? ExternalId { get; set; }
    public string Name { get; set; } = default!;
    /// <summary>Path within the source, preserved under the blob prefix.</summary>
    public string RelativePath { get; set; } = default!;
    public string SourceLocation { get; set; } = default!;
    public string? ContentType { get; set; }
    public long SizeBytes { get; set; }
    public string? ChecksumSha256 { get; set; }
    /// <summary>Content-MD5 as returned by blob storage for the current blob. Drives change detection.</summary>
    public string? ContentMd5 { get; set; }
    public string? BlobETag { get; set; }
    /// <summary>The version row describing the blob this document currently points at.</summary>
    public Guid? CurrentVersionId { get; set; }
    /// <summary>When the vector service finished with the current version.</summary>
    public DateTimeOffset? ProcessedAt { get; set; }
    public int? ChunkCount { get; set; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;
    public string? BlobPath { get; set; }
    public string? BlobUrl { get; set; }
    public DateTimeOffset? BlobUploadedAt { get; set; }
    public string? Error { get; set; }
    public int AttemptCount { get; set; }
    /// <summary>Bumped each time a recurring sync replaces this document's content.</summary>
    public int Revision { get; set; } = 1;
    public DateTimeOffset? LastSyncedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One row per blob actually written for a document: its name, where it came
/// from, where it landed, and the hash storage returned for it.
///
/// Kept apart from <see cref="UploadedDocument"/> (which is the document's
/// current state) so there is a history to compare against — a recurring update
/// asks "is this the same bytes as the latest version?" and the answer is here.
/// </summary>
public class DocumentVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UploadedDocumentId { get; set; }
    public UploadedDocument UploadedDocument { get; set; } = default!;
    public Guid SourceDocumentId { get; set; }
    public Guid ArtifactId { get; set; }
    public int Revision { get; set; }

    public string Name { get; set; } = default!;
    public string RelativePath { get; set; } = default!;
    public string SourceLocation { get; set; } = default!;
    public SourceType SourceType { get; set; }
    public string? ExternalId { get; set; }
    public string? ContentType { get; set; }
    public long SizeBytes { get; set; }

    public string BlobContainer { get; set; } = default!;
    public string BlobPath { get; set; } = default!;
    public string BlobUrl { get; set; } = default!;
    public string? BlobETag { get; set; }
    /// <summary>Content-MD5 returned by blob storage on upload, base64.</summary>
    public string ContentMd5 { get; set; } = default!;
    /// <summary>SHA-256 computed while streaming, hex. Independent of the store.</summary>
    public string ContentSha256 { get; set; } = default!;
    public DateTimeOffset BlobLastModified { get; set; }
    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Where an artifact stands, as reported by the vector service.</summary>
public enum ArtifactStatus { Empty = 0, Pending = 1, Processing = 2, Processed = 3, PartiallyProcessed = 4, Failed = 5 }

/// <summary>
/// One person clearing a notification. Notifications are broadcast to the whole
/// organization, so clearing hides it for that person only — never for anyone else.
/// </summary>
public class NotificationDismissal
{
    public Guid NotificationId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset DismissedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    /// <summary>Null = broadcast to the whole organization.</summary>
    public Guid? UserId { get; set; }
    public string Type { get; set; } = default!;
    public string Title { get; set; } = default!;
    public string Body { get; set; } = default!;
    public NotificationSeverity Severity { get; set; } = NotificationSeverity.Info;
    public string? EntityType { get; set; }
    public Guid? EntityId { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A standing instruction to re-import a SharePoint, Google Drive or GitHub location on a
/// cadence and refresh the vectors for documents that changed. Only those two
/// sources qualify: both expose a stable, listable folder that can be re-read, so a
/// later run can match what it finds against what the artifact already holds.
/// </summary>
public class RecurringSyncSchedule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid ArtifactId { get; set; }
    public Artifact Artifact { get; set; } = default!;

    /// <summary>One of <see cref="SyncScheduleEndpoints.Syncable"/>: SharePoint, Google Drive or GitHub.</summary>
    public SourceType SourceType { get; set; }
    /// <summary>
    /// The grant the sync runs under. Required for SharePoint and Google Drive; empty
    /// for a public GitHub repository, which needs no token.
    /// </summary>
    public Guid? SourceConnectionId { get; set; }
    public string SourceReference { get; set; } = default!;
    /// <summary>The same connector payload the original upload used (driveId/itemId, folderId).</summary>
    public JsonDocument? SourceOptions { get; set; }

    public SyncFrequency Frequency { get; set; }
    /// <summary>Wall-clock time of day in <see cref="TimeZoneId"/>, not UTC.</summary>
    public TimeOnly TimeOfDay { get; set; }
    /// <summary>IANA id, so "09:00 daily" keeps meaning 09:00 across DST changes.</summary>
    public string TimeZoneId { get; set; } = "UTC";
    /// <summary>0=Sunday … 6=Saturday. Weekly only.</summary>
    public int? DayOfWeek { get; set; }
    /// <summary>1–28. Monthly only — capped so every month has the day.</summary>
    public int? DayOfMonth { get; set; }

    public SyncScheduleStatus Status { get; set; } = SyncScheduleStatus.Active;
    public DateTimeOffset NextRunAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public Guid? LastSourceDocumentId { get; set; }
    /// <summary>Claim flag: the worker sets it so a slow run cannot be started twice.</summary>
    public bool IsRunning { get; set; }
    public DateTimeOffset? RunStartedAt { get; set; }

    public int DocumentsAddedLastRun { get; set; }
    public int DocumentsUpdatedLastRun { get; set; }
    public int DocumentsUnchangedLastRun { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string? LastError { get; set; }

    public Guid CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}


/// <summary>
/// A queued message, used when no Azure Service Bus namespace is configured.
/// Consumers claim one row at a time with SKIP LOCKED, so the local stack has
/// the same one-at-a-time semantics as the broker it stands in for.
/// </summary>
public class QueueMessage
{
    public long Id { get; set; }
    public string Queue { get; set; } = default!;
    public string MessageId { get; set; } = default!;
    public string Subject { get; set; } = default!;
    public string Payload { get; set; } = default!;
    public string? SessionId { get; set; }
    public QueueMessageStatus Status { get; set; } = QueueMessageStatus.Pending;
    public int DeliveryCount { get; set; }
    /// <summary>Visibility timeout: another consumer may take the row once this passes.</summary>
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
}


/// <summary>
/// One person's conversation with the knowledge base, inside one organization.
/// The scope is resolved to artifacts on every turn, so documents added to a team
/// later are searched too.
/// </summary>
public class ChatConversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public string Title { get; set; } = "New conversation";
    public ChatScope Scope { get; set; } = ChatScope.Organization;
    public Guid? ScopeId { get; set; }
    /// <summary>The scope's name when it was chosen, for the conversation list.</summary>
    public string ScopeLabel { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<ChatMessage> Messages { get; set; } = [];
}

public class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public ChatConversation Conversation { get; set; } = default!;
    public ChatRole Role { get; set; }
    public string Content { get; set; } = "";
    /// <summary>The numbered sources the answer was written from (jsonb), as the RAG platform returned them.</summary>
    public string? Sources { get; set; }
    /// <summary>Source numbers the answer actually cites (jsonb array).</summary>
    public string? Cited { get; set; }
    public string? Model { get; set; }
    /// <summary>Token and cost accounting for the turn (jsonb).</summary>
    public string? Usage { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
