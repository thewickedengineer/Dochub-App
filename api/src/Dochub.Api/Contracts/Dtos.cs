using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dochub.Api.Contracts;

// ── Auth ──────────────────────────────────────────────────────────────────────
public record SsoSignInRequest(string Provider, string IdToken, Guid? OrganizationId);

public record AuthResponse(
    string AccessToken, DateTimeOffset ExpiresAt, UserDto User,
    OrganizationDto? Organization, IReadOnlyList<OrganizationDto> Organizations);

public record UserDto(
    Guid Id, string Email, string DisplayName, string? AvatarUrl,
    string IdentityProvider, bool CanCreateOrganizations, string? Role, bool IsCreator = false);

public record SwitchOrganizationRequest(Guid OrganizationId);

// ── Organization ──────────────────────────────────────────────────────────────
public record OrganizationDto(Guid Id, string Name, string Slug, string Initials, string Plan, string Role, int MemberCount);

/// <summary>
/// <paramref name="OwnerLoginId"/> is the owner's sign-in name — the address they
/// sign in with to Microsoft (work, school or personal account) or Google. Empty
/// makes the caller the owner.
/// </summary>
public record CreateOrganizationRequest(string Name, string? Plan, string? OwnerLoginId = null, string? OwnerDisplayName = null);

// ── Platform (Creator) ────────────────────────────────────────────────────────
public record OwnerDto(Guid UserId, string LoginId, string DisplayName, string IdentityProvider, bool HasSignedIn);
public record PlatformOrganizationDto(
    Guid Id, string Name, string Slug, string Initials, string Plan, int MemberCount,
    IReadOnlyList<OwnerDto> Owners, string? CreatedBy, DateTimeOffset CreatedAt);
public record AddOwnerRequest(string LoginId, string? DisplayName);

public record MemberDto(
    Guid UserId, string Name, string Email, string Role, string IdentityProvider,
    bool CanCreateOrganizations, IReadOnlyList<string> Teams, DateTimeOffset JoinedAt);

public record AddMemberRequest(string Email, string Role, bool? CanCreateOrganizations, string? DisplayName);

public record UpdateMemberRequest(string? Role, bool? CanCreateOrganizations);

// ── Structure ─────────────────────────────────────────────────────────────────
public record CreateTeamRequest(string Name, string? Description);
public record CreateGroupRequest(string Name, string? Description);
public record CreateArtifactRequest(string Name, string Category, string PrimarySource, string? Description);

public record TeamDto(Guid Id, string Name, string Slug, string? Description, int GroupCount, int ArtifactCount, IReadOnlyList<GroupDto> Groups);
public record GroupDto(Guid Id, string Name, string Slug, string? Description, IReadOnlyList<ArtifactSummaryDto> Artifacts);

public record ArtifactSummaryDto(
    Guid Id, string Name, string Slug, string Category, string PrimarySource,
    Guid GroupId, string GroupName, Guid TeamId, string TeamName,
    int TotalDocuments, int IndexedDocuments, int PendingDocuments,
    int ProcessingDocuments, int FailedDocuments, string Status,
    /// <summary>Stored artifact status, set by the RAG platform's artifact callback.</summary>
    string ProcessingStatus, DateTimeOffset? LastProcessedAt);

public record ArtifactDetailDto(
    ArtifactSummaryDto Artifact, IReadOnlyList<DocumentDto> Documents, IReadOnlyList<SourceDocumentDto> Sources);

// ── Processing ────────────────────────────────────────────────────────────────
/// <summary>
/// One source the user committed to. Nothing reached the backend before this.
/// <paramref name="Options"/> carries the connector payload: repository/branch/path
/// for GitHub, driveId/itemId for SharePoint, folderId for Drive, stagingIds for local.
/// </summary>
public record SourceSubmissionRequest(
    string SourceType,
    string SourceReference,
    Guid? SourceConnectionId,
    JsonElement? Options,
    IReadOnlyList<DeclaredDocument>? Documents,
    /// <summary>Omit for a one-time import; supply it to also keep the location in sync.</summary>
    SyncScheduleRequest? Schedule);

public record DeclaredDocument(string Name, string RelativePath, long? SizeBytes, string? ContentType);

/// <summary>The Process button's payload — every source staged in the browser.</summary>
public record ProcessRequest(IReadOnlyList<SourceSubmissionRequest> Sources);

public record ProcessAcknowledgement(
    IReadOnlyList<SourceDocumentDto> Sources,
    IReadOnlyList<SyncScheduleDto> Schedules,
    string Message);

public record SourceDocumentDto(
    Guid Id, string Reference, Guid ArtifactId, string ArtifactName, string Path,
    string SourceType, string SourceReference, string Status, string StatusLabel,
    int TotalDocuments, int UploadedDocuments, int ProcessedDocuments, int FailedDocuments,
    int ProgressPercent, string? BlobContainer, string? BlobPrefix, string RequestedBy,
    bool IsScheduled, DateTimeOffset RequestedAt, DateTimeOffset? UploadStartedAt,
    DateTimeOffset? UploadedAt, DateTimeOffset? ProcessingStartedAt, DateTimeOffset? ProcessedAt,
    string? Error);

public record SourceDocumentDetailDto(SourceDocumentDto Source, IReadOnlyList<DocumentDto> Documents);

public record DocumentDto(
    Guid Id, Guid SourceDocumentId, string Name, string RelativePath, string SourceLocation,
    string SourceType, string Status, long SizeBytes, string? ContentType,
    string? BlobPath, string? BlobUrl, DateTimeOffset? BlobUploadedAt,
    string? Error, int Revision, DateTimeOffset? LastSyncedAt,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    /// <summary>Base64 MD5 Azure returned for the current blob.</summary>
    string? ContentMd5, int? ChunkCount, DateTimeOffset? ProcessedAt);

public record StagedFileDto(
    string StagingId, string Name, string? RelativePath, long SizeBytes, string? ContentType);

// ── Scheduled updates ─────────────────────────────────────────────────────────
/// <summary>
/// Opt-in recurring refresh, supplied alongside a source. Only valid for
/// SharePoint and Google Drive — the two sources whose locations can be re-listed.
/// </summary>
public record SyncScheduleRequest(
    string Frequency,
    string TimeOfDay,
    string? TimeZoneId,
    int? DayOfWeek,
    int? DayOfMonth);

public record SyncScheduleDto(
    Guid Id, Guid ArtifactId, string ArtifactName, string Path, string SourceType,
    string SourceReference, string Frequency, string TimeOfDay, string TimeZoneId,
    int? DayOfWeek, int? DayOfMonth, string Status, string Cadence,
    DateTimeOffset NextRunAt, DateTimeOffset? LastRunAt,
    int DocumentsAddedLastRun, int DocumentsUpdatedLastRun, int DocumentsUnchangedLastRun,
    int ConsecutiveFailures, string? LastError, string CreatedBy, DateTimeOffset CreatedAt);

public record UpdateSyncScheduleRequest(
    string? Frequency, string? TimeOfDay, string? TimeZoneId,
    int? DayOfWeek, int? DayOfMonth, string? Status);

// ── Knowledge base ────────────────────────────────────────────────────────────
public record KnowledgeBaseDto(
    KnowledgeBaseStatsDto Stats,
    IReadOnlyList<ArtifactSummaryDto> Artifacts,
    IReadOnlyList<SourceBreakdownDto> BySource,
    DateTimeOffset? LastIndexedAt);

public record KnowledgeBaseStatsDto(
    int TotalDocuments, int Indexed, int Pending, int Processing, int Failed,
    int Teams, int Groups, int Artifacts, int CoveragePercent);

public record SourceBreakdownDto(string SourceType, string Label, int Count, int Percent);

// ── Connections ───────────────────────────────────────────────────────────────
public record SourceConnectionDto(
    Guid Id, string SourceType, string DisplayName, string Status,
    DateTimeOffset? ExpiresAt, string? Scopes, DateTimeOffset UpdatedAt,
    string? Account, bool CanRefresh,
    /// <summary>Scheduled syncs that run under this connection, so removing it can say what stops.</summary>
    int ScheduleCount = 0);

/// <summary>A source that can be connected, and how: a sign-in window, or a pasted token.</summary>
public record ConnectionOptionDto(string SourceType, string Label, bool SignIn, bool SignInConfigured, bool AcceptsToken);

/// <summary>Kicks off the consent flow; the browser opens the returned URL in a popup.</summary>
public record StartOAuthResponse(
    string AuthorizeUrl, string Provider, string SourceType,
    /// <summary>Echoed so a mismatch with the provider's registration is easy to spot.</summary>
    string RedirectUri);

/// <summary>What the popup posts back once the provider redirects to /oauth/callback.</summary>
public record CompleteOAuthRequest(string Code, string State);

public record ResolveLinkRequest(string Link, Guid SourceConnectionId);

public record ResolvedLinkDto(
    string SourceType, string DisplayName, string SourceReference,
    Dictionary<string, object?> Options);

public record ConnectSourceRequest(
    string SourceType, string AccessToken, string? RefreshToken,
    DateTimeOffset? ExpiresAt, string? Scopes, string? DisplayName);

// ── Notifications ─────────────────────────────────────────────────────────────
public record NotificationDto(
    Guid Id, string Type, string Title, string Body, string Severity,
    string? EntityType, Guid? EntityId, bool Read, DateTimeOffset CreatedAt);

public record NotificationListDto(IReadOnlyList<NotificationDto> Items, int UnreadCount);

// ── Errors ────────────────────────────────────────────────────────────────────
public record ApiError(string Code, string Message, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Details = null);

// ── Chat ──────────────────────────────────────────────────────────────────────
/// <summary>Scope is Organization (default), Team, Group or Artifact; ScopeId names the latter three.</summary>
public record CreateChatConversationRequest(string? Title, string? Scope, Guid? ScopeId);
public record UpdateChatConversationRequest(string? Title, string? Scope, Guid? ScopeId);
public record SendChatMessageRequest(string Content);

public record ChatConversationDto(
    Guid Id, string Title, string Scope, Guid? ScopeId, string ScopeLabel,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public record ChatMessageDto(
    Guid Id, string Role, string Content, System.Text.Json.JsonElement? Sources, int[]? Cited,
    string? Model, string? Error, DateTimeOffset CreatedAt);

public record ChatConversationDetailDto(ChatConversationDto Conversation, IReadOnlyList<ChatMessageDto> Messages);
