import type {
  ArtifactDetailDto, ArtifactSummaryDto, AuthResponse, ChatConversationDetailDto, ChatConversationDto,
  ChatEvent, ChatScope, KnowledgeBaseDto, MemberDto, OwnerDto, PlatformOrganizationDto,
  NotificationListDto, OrganizationDto, PendingSource, ProcessAcknowledgement,
  ResolvedLinkDto, SourceConnectionDto, SourceDocumentDetailDto, SourceDocumentDto,
  StagedFileDto, StartOAuthResponse, ConnectionOptionDto, BrowseResult, SyncScheduleDto, SyncScheduleRequest, SyncRunResult, TeamDto,
} from './types';

const BASE = import.meta.env.VITE_API_URL ?? 'http://localhost:5080';
const TOKEN_KEY = 'dochub.token';

export const tokenStore = {
  get: () => localStorage.getItem(TOKEN_KEY),
  set: (token: string) => localStorage.setItem(TOKEN_KEY, token),
  clear: () => localStorage.removeItem(TOKEN_KEY),
};

export class ApiError extends Error {
  readonly status: number;
  readonly code: string;

  constructor(status: number, code: string, message: string) {
    super(message);
    this.status = status;
    this.code = code;
  }
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const token = tokenStore.get();
  const headers = new Headers(init.headers);
  if (token) headers.set('Authorization', `Bearer ${token}`);
  if (init.body && !(init.body instanceof FormData)) headers.set('Content-Type', 'application/json');

  const response = await fetch(`${BASE}${path}`, { ...init, headers });

  if (response.status === 204) return undefined as T;

  const text = await response.text();
  const payload = text ? JSON.parse(text) : null;

  if (!response.ok) {
    // A stale token should drop the session rather than loop on 401s.
    if (response.status === 401) tokenStore.clear();
    throw new ApiError(response.status, payload?.code ?? 'error',
      payload?.message ?? `Request failed with ${response.status}.`);
  }
  return payload as T;
}

const json = (body: unknown) => JSON.stringify(body);

/**
 * POSTs and reads a server-sent event stream. EventSource cannot send a body or
 * an Authorization header, so this parses the `data:` lines from fetch itself.
 */
async function stream(path: string, body: unknown, onEvent: (event: ChatEvent) => void, signal?: AbortSignal) {
  const token = tokenStore.get();
  const response = await fetch(`${BASE}${path}`, {
    method: 'POST', body: json(body), signal,
    headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) },
  });
  if (!response.ok || !response.body) {
    const payload = await response.json().catch(() => null);
    if (response.status === 401) tokenStore.clear();
    throw new ApiError(response.status, payload?.code ?? 'error', payload?.message ?? `Request failed with ${response.status}.`);
  }
  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  for (;;) {
    const { value, done } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });
    let end: number;
    while ((end = buffer.indexOf('\n\n')) >= 0) {
      const frame = buffer.slice(0, end);
      buffer = buffer.slice(end + 2);
      for (const line of frame.split('\n')) {
        if (line.startsWith('data: ')) onEvent(JSON.parse(line.slice(6)) as ChatEvent);
      }
    }
  }
}

export const api = {
  baseUrl: BASE,

  // Auth
  signIn: (provider: string, idToken: string, organizationId?: string) =>
    request<AuthResponse>('/api/auth/sso', { method: 'POST', body: json({ provider, idToken, organizationId }) }),
  me: () => request<AuthResponse>('/api/auth/me'),
  switchOrganization: (organizationId: string) =>
    request<AuthResponse>('/api/auth/switch-organization', { method: 'POST', body: json({ organizationId }) }),

  // Organizations
  organizations: () => request<OrganizationDto[]>('/api/organizations'),
  /** ownerLoginId: the address the owner signs in with; omit to be the owner yourself. */
  createOrganization: (name: string, ownerLoginId?: string, ownerDisplayName?: string) =>
    request<OrganizationDto>('/api/organizations', {
      method: 'POST', body: json({ name, ownerLoginId: ownerLoginId || undefined, ownerDisplayName: ownerDisplayName || undefined }),
    }),

  // Platform (Creator only)
  platformOrganizations: () => request<PlatformOrganizationDto[]>('/api/platform/organizations'),
  addOwner: (organizationId: string, loginId: string, displayName?: string) =>
    request<OwnerDto>(`/api/platform/organizations/${organizationId}/owners`,
      { method: 'POST', body: json({ loginId, displayName: displayName || undefined }) }),
  removeOwner: (organizationId: string, userId: string) =>
    request<void>(`/api/platform/organizations/${organizationId}/owners/${userId}`, { method: 'DELETE' }),
  members: () => request<MemberDto[]>('/api/organizations/current/members'),
  addMember: (email: string, role: string) =>
    request<MemberDto>('/api/organizations/current/members', { method: 'POST', body: json({ email, role }) }),

  // Workspace
  teams: () => request<TeamDto[]>('/api/teams'),
  createTeam: (name: string, description?: string) =>
    request<TeamDto>('/api/teams', { method: 'POST', body: json({ name, description }) }),
  createGroup: (teamId: string, name: string, description?: string) =>
    request<unknown>(`/api/teams/${teamId}/groups`, { method: 'POST', body: json({ name, description }) }),
  createArtifact: (groupId: string, name: string, category: string, primarySource: string) =>
    request<ArtifactSummaryDto>(`/api/groups/${groupId}/artifacts`,
      { method: 'POST', body: json({ name, category, primarySource }) }),
  artifacts: () => request<ArtifactSummaryDto[]>('/api/artifacts'),
  artifact: (id: string) => request<ArtifactDetailDto>(`/api/artifacts/${id}`),
  knowledgeBase: () => request<KnowledgeBaseDto>('/api/knowledge-base'),

  // Staging + processing
  /** Streams local bytes to the server. Nothing is recorded against an artifact yet. */
  stage: (files: File[]) => {
    const form = new FormData();
    files.forEach((file, index) => {
      const field = `file${index}`;
      form.append(field, file, file.name);
      // Folder picks carry webkitRelativePath; keep it so the tree survives.
      const relative = (file as File & { webkitRelativePath?: string }).webkitRelativePath;
      form.append(`path:${field}`, relative && relative.length > 0 ? relative : file.name);
    });
    return request<StagedFileDto[]>('/api/uploads/staging', { method: 'POST', body: form });
  },

  /** The only call the Upload screen makes. Everything else was client-side. */
  process: (artifactId: string, sources: PendingSource[]) =>
    request<ProcessAcknowledgement>(`/api/artifacts/${artifactId}/process`, {
      method: 'POST',
      body: json({
        sources: sources.map(s => ({
          sourceType: s.sourceType,
          sourceReference: s.sourceReference,
          sourceConnectionId: s.sourceConnectionId,
          options: s.options,
          documents: s.documents,
          schedule: s.schedule,
        })),
      }),
    }),

  sourceDocuments: (limit = 50, artifactId?: string) =>
    request<SourceDocumentDto[]>(
      `/api/source-documents?limit=${limit}${artifactId ? `&artifactId=${artifactId}` : ''}`),

  sourceDocument: (id: string) => request<SourceDocumentDetailDto>(`/api/source-documents/${id}`),

  /** Remove a failed upload: its index entries, files and rows. Safe to call again if it stopped partway. */
  removeSourceDocument: (id: string) =>
    request<{ reference: string; documentsRemoved: number; documentsRestored: number; blobsDeleted: number; reindexed: number }>(
      `/api/source-documents/${id}`, { method: 'DELETE' }),

  retrySourceDocument: (id: string) =>
    request<{ reference: string; message: string }>(`/api/source-documents/${id}/retry`, { method: 'POST' }),

  // Scheduled updates
  syncSchedules: () => request<SyncScheduleDto[]>('/api/sync-schedules'),
  updateSyncSchedule: (id: string, body: Partial<SyncScheduleRequest> & { status?: string }) =>
    request<SyncScheduleDto>(`/api/sync-schedules/${id}`, { method: 'PATCH', body: json(body) }),
  deleteSyncSchedule: (id: string) =>
    request<void>(`/api/sync-schedules/${id}`, { method: 'DELETE' }),
  runSyncSchedule: (id: string) =>
    request<SyncRunResult>(`/api/sync-schedules/${id}/run`, { method: 'POST' }),

  // Connections
  connections: () => request<SourceConnectionDto[]>('/api/connections'),
  /** Which sources can be connected, and whether each has a sign-in window set up. */
  connectionOptions: () => request<ConnectionOptionDto[]>('/api/connections/options'),
  /** Asks the API for the provider's consent URL; the caller opens it in a popup. */
  startOAuth: (sourceType: string) =>
    request<StartOAuthResponse>(`/api/connections/oauth/${sourceType}/start`),

  /** Hands the popup's authorization code back for a server-side exchange. */
  completeOAuth: (code: string, state: string) =>
    request<SourceConnectionDto>('/api/connections/oauth/callback', {
      method: 'POST', body: json({ code, state }),
    }),

  /** Turns a pasted Drive or SharePoint link into the ids a connector needs. */
  resolveLink: (link: string, sourceConnectionId: string) =>
    request<ResolvedLinkDto>('/api/connections/resolve-link', {
      method: 'POST', body: json({ link, sourceConnectionId }),
    }),

  /** Lists what a connected SharePoint / OneDrive or Google account can see at a location. */
  browse: (connectionId: string, location: string, q?: string, cursor?: string) => {
    const params = new URLSearchParams({ location });
    if (q) params.set('q', q);
    if (cursor) params.set('cursor', cursor);
    return request<BrowseResult>(`/api/connections/${connectionId}/browse?${params}`);
  },

  disconnect: (connectionId: string) => request<void>(`/api/connections/${connectionId}`, { method: 'DELETE' }),

  connect: (sourceType: string, accessToken: string, extra?: { scopes?: string; expiresAt?: string }) =>
    request<SourceConnectionDto>('/api/connections',
      { method: 'POST', body: json({ sourceType, accessToken, ...extra }) }),

  // Chat
  conversations: () => request<ChatConversationDto[]>('/api/chat/conversations'),
  conversation: (id: string) => request<ChatConversationDetailDto>(`/api/chat/conversations/${id}`),
  createConversation: (scope: ChatScope, scopeId?: string, title?: string) =>
    request<ChatConversationDto>('/api/chat/conversations', { method: 'POST', body: json({ scope, scopeId, title }) }),
  updateConversation: (id: string, body: { title?: string; scope?: ChatScope; scopeId?: string }) =>
    request<ChatConversationDto>(`/api/chat/conversations/${id}`, { method: 'PATCH', body: json(body) }),
  deleteConversation: (id: string) => request<void>(`/api/chat/conversations/${id}`, { method: 'DELETE' }),
  /** Streams the answer: started, sources, delta…, done — or error. */
  ask: (id: string, content: string, onEvent: (event: ChatEvent) => void, signal?: AbortSignal) =>
    stream(`/api/chat/conversations/${id}/messages`, { content }, onEvent, signal),

  // Notifications
  notifications: (limit = 50) => request<NotificationListDto>(`/api/notifications?limit=${limit}`),
  clearNotification: (id: string) => request<void>(`/api/notifications/${id}`, { method: 'DELETE' }),
  clearNotifications: () => request<{ cleared: number }>('/api/notifications', { method: 'DELETE' }),
  markRead: (ids?: string[]) =>
    request<{ markedRead: number }>('/api/notifications/read', { method: 'POST', body: json(ids ?? null) }),
};
