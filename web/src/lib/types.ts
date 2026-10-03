export type OrgRole = 'Member' | 'Admin' | 'Owner';

export interface UserDto {
  id: string; email: string; displayName: string; avatarUrl?: string;
  identityProvider: string; canCreateOrganizations: boolean; role?: OrgRole;
  /** Platform Creator: creates organizations and assigns their owners. */
  isCreator?: boolean;
}
export interface OrganizationDto {
  id: string; name: string; slug: string; initials: string;
  plan: string; role: OrgRole; memberCount: number;
}
export interface AuthResponse {
  accessToken: string; expiresAt: string; user: UserDto;
  organization?: OrganizationDto; organizations: OrganizationDto[];
}
export interface ArtifactSummaryDto {
  id: string; name: string; slug: string; category: string; primarySource: string;
  groupId: string; groupName: string; teamId: string; teamName: string;
  totalDocuments: number; indexedDocuments: number; pendingDocuments: number;
  processingDocuments: number; failedDocuments: number; status: string;
  /** Stored by the API when the RAG platform reports the artifact done. */
  processingStatus: 'Empty' | 'Pending' | 'Processing' | 'Processed' | 'PartiallyProcessed' | 'Failed';
  lastProcessedAt?: string;
}
export interface GroupDto { id: string; name: string; slug: string; description?: string; artifacts: ArtifactSummaryDto[] }
export interface TeamDto {
  id: string; name: string; slug: string; description?: string;
  groupCount: number; artifactCount: number; groups: GroupDto[];
}
export type SourceDocumentStatus =
  | 'RequestUpload' | 'Uploading' | 'Uploaded'
  | 'Processing' | 'Processed' | 'PartiallyFailed' | 'Failed' | 'Cancelled' | 'Removing';

export interface DocumentDto {
  id: string; sourceDocumentId: string; name: string; relativePath: string; sourceLocation: string;
  sourceType: string; status: string; sizeBytes: number; contentType?: string;
  blobPath?: string; blobUrl?: string; blobUploadedAt?: string;
  error?: string; revision: number; lastSyncedAt?: string;
  createdAt: string; updatedAt: string;
  contentMd5?: string; chunkCount?: number; processedAt?: string;
}

export interface SourceDocumentDto {
  id: string; reference: string; artifactId: string; artifactName: string; path: string;
  sourceType: string; sourceReference: string;
  status: SourceDocumentStatus; statusLabel: string;
  totalDocuments: number; uploadedDocuments: number;
  processedDocuments: number; failedDocuments: number;
  progressPercent: number;
  blobContainer?: string; blobPrefix?: string;
  requestedBy: string; isScheduled: boolean;
  requestedAt: string; uploadStartedAt?: string; uploadedAt?: string;
  processingStartedAt?: string; processedAt?: string;
  error?: string;
}

export interface SourceDocumentDetailDto {
  source: SourceDocumentDto;
  documents: DocumentDto[];
}

/** One source the browser is holding, not yet sent anywhere. */
export interface PendingSource {
  /** Client-side id only — the backend never sees it. */
  key: string;
  sourceType: string;
  sourceReference: string;
  sourceConnectionId?: string;
  options?: Record<string, unknown>;
  documents?: { name: string; relativePath: string; sizeBytes?: number; contentType?: string }[];
  schedule?: SyncScheduleRequest;
  /** Shown while the source sits in the staged list. */
  summary: string;
}

export interface ProcessAcknowledgement {
  sources: SourceDocumentDto[];
  schedules: SyncScheduleDto[];
  message: string;
}

export interface ArtifactDetailDto {
  artifact: ArtifactSummaryDto;
  documents: DocumentDto[];
  sources: SourceDocumentDto[];
}

export type SyncFrequency = 'Daily' | 'Weekly' | 'Monthly';

export interface SyncScheduleRequest {
  frequency: SyncFrequency;
  timeOfDay: string;
  timeZoneId?: string;
  dayOfWeek?: number;
  dayOfMonth?: number;
}

export interface SyncScheduleDto {
  id: string; artifactId: string; artifactName: string; path: string;
  sourceType: string; sourceReference: string;
  frequency: SyncFrequency; timeOfDay: string; timeZoneId: string;
  dayOfWeek?: number; dayOfMonth?: number;
  status: 'Active' | 'Paused' | 'Failed';
  cadence: string;
  nextRunAt: string; lastRunAt?: string;
  documentsAddedLastRun: number; documentsUpdatedLastRun: number; documentsUnchangedLastRun: number;
  consecutiveFailures: number; lastError?: string;
  createdBy: string; createdAt: string;
}

/** POST /sync-schedules/{id}/run returns the source it queued. */
export interface SyncRunResult {
  sourceDocumentId: string;
  reference: string;
  message: string;
}

export interface KnowledgeBaseStatsDto {
  totalDocuments: number; indexed: number; pending: number; processing: number; failed: number;
  teams: number; groups: number; artifacts: number; coveragePercent: number;
}
export interface SourceBreakdownDto { sourceType: string; label: string; count: number; percent: number }
export interface KnowledgeBaseDto {
  stats: KnowledgeBaseStatsDto; artifacts: ArtifactSummaryDto[];
  bySource: SourceBreakdownDto[]; lastIndexedAt?: string;
}
export interface MemberDto {
  userId: string; name: string; email: string; role: OrgRole; identityProvider: string;
  canCreateOrganizations: boolean; teams: string[]; joinedAt: string;
}
export interface SourceConnectionDto {
  id: string; sourceType: string; displayName: string; status: string;
  expiresAt?: string; scopes?: string; updatedAt: string;
  /** The account that consented, so two connections can be told apart. */
  account?: string;
  /** True when a refresh token is stored, so the connection renews itself. */
  canRefresh: boolean;
}

export interface StartOAuthResponse {
  authorizeUrl: string;
  provider: 'Google' | 'Microsoft';
  sourceType: string;
}

export interface ResolvedLinkDto {
  sourceType: string;
  displayName: string;
  sourceReference: string;
  options: Record<string, unknown>;
}
export interface NotificationDto {
  id: string; type: string; title: string; body: string; severity: string;
  entityType?: string; entityId?: string; read: boolean; createdAt: string;
}
export interface NotificationListDto { items: NotificationDto[]; unreadCount: number }
export interface StagedFileDto {
  stagingId: string; name: string; relativePath?: string; sizeBytes: number; contentType?: string;
}
export interface ApiErrorBody { code: string; message: string; details?: unknown }

// ── Chat ──────────────────────────────────────────────────────────────────────
export type ChatScope = 'Organization' | 'Team' | 'Group' | 'Artifact';

export interface ChatConversationDto {
  id: string; title: string; scope: ChatScope; scopeId?: string; scopeLabel: string;
  createdAt: string; updatedAt: string;
}

/** One numbered source an answer was written from, as the RAG platform returned it. */
export interface ChatSource {
  n: number; chunk_id: string; document_id: string; dochub_document_id?: string; artifact_id?: string;
  title?: string; filename?: string; heading_path: string[]; location?: string;
  chunk_type: string; snippet: string; score: number;
}

export interface ChatMessageDto {
  id: string; role: 'User' | 'Assistant'; content: string;
  sources?: ChatSource[]; cited?: number[]; model?: string; error?: string; createdAt: string;
}

export interface ChatConversationDetailDto { conversation: ChatConversationDto; messages: ChatMessageDto[] }

export type ChatEvent =
  | { type: 'started'; questionId: string; answerId: string; scope: string; title: string }
  | { type: 'sources'; query: string; sources: ChatSource[] }
  | { type: 'delta'; text: string }
  | { type: 'done'; cited: number[]; model?: string; usage?: Record<string, number> }
  | { type: 'error'; error: string; detail?: string };

// ── Platform (Creator) ────────────────────────────────────────────────────────
export interface OwnerDto { userId: string; loginId: string; displayName: string; identityProvider: string; hasSignedIn: boolean }
export interface PlatformOrganizationDto {
  id: string; name: string; slug: string; initials: string; plan: string; memberCount: number;
  owners: OwnerDto[]; createdBy?: string; createdAt: string;
}
