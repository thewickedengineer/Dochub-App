import type {
  ArtifactDetailDto, ArtifactSummaryDto, AuthResponse, KnowledgeBaseDto, MemberDto,
  NotificationListDto, OrganizationDto, PendingSource, ProcessAcknowledgement,
  ResolvedLinkDto, SourceConnectionDto, SourceDocumentDetailDto, SourceDocumentDto,
  StagedFileDto, StartOAuthResponse, SyncScheduleDto, SyncScheduleRequest, SyncRunResult, TeamDto,
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
  createOrganization: (name: string, plan?: string) =>
    request<OrganizationDto>('/api/organizations', { method: 'POST', body: json({ name, plan }) }),
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

  connect: (sourceType: string, accessToken: string, extra?: { scopes?: string; expiresAt?: string }) =>
    request<SourceConnectionDto>('/api/connections',
      { method: 'POST', body: json({ sourceType, accessToken, ...extra }) }),

  // Notifications
  notifications: (limit = 50) => request<NotificationListDto>(`/api/notifications?limit=${limit}`),
  markRead: (ids?: string[]) =>
    request<{ markedRead: number }>('/api/notifications/read', { method: 'POST', body: json(ids ?? null) }),
};
