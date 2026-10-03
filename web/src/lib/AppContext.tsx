import {
  createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode,
} from 'react';
import { HubConnectionBuilder, HubConnectionState, type HubConnection } from '@microsoft/signalr';
import { api, tokenStore } from './api';
import type { AuthResponse, NotificationDto, OrganizationDto, UserDto } from './types';

export interface Toast {
  id: number; icon: string; dot: string; title: string; body: string;
}

interface AppState {
  ready: boolean;
  user: UserDto | null;
  organization: OrganizationDto | null;
  organizations: OrganizationDto[];
  notifications: NotificationDto[];
  unread: number;
  toasts: Toast[];
  /** Bumped whenever server state changes, so screens can re-fetch. */
  revision: number;
  signIn: (provider: string, idToken: string) => Promise<void>;
  signOut: () => void;
  switchOrganization: (id: string) => Promise<void>;
  refreshSession: () => Promise<void>;
  refreshNotifications: () => Promise<void>;
  markNotificationsRead: () => Promise<void>;
  /** Hides notifications for this user only; omit the id to clear them all. */
  clearNotifications: (id?: string) => Promise<void>;
  showToast: (toast: Omit<Toast, 'id'>) => void;
  invalidate: () => void;
}

const Ctx = createContext<AppState | null>(null);

export function AppProvider({ children }: { children: ReactNode }) {
  const [ready, setReady] = useState(false);
  const [user, setUser] = useState<UserDto | null>(null);
  const [organization, setOrganization] = useState<OrganizationDto | null>(null);
  const [organizations, setOrganizations] = useState<OrganizationDto[]>([]);
  const [notifications, setNotifications] = useState<NotificationDto[]>([]);
  const [unread, setUnread] = useState(0);
  const [toasts, setToasts] = useState<Toast[]>([]);
  const [revision, setRevision] = useState(0);
  const hub = useRef<HubConnection | null>(null);
  const toastId = useRef(0);

  const invalidate = useCallback(() => setRevision(r => r + 1), []);

  const showToast = useCallback((toast: Omit<Toast, 'id'>) => {
    const id = ++toastId.current;
    setToasts(current => [...current, { ...toast, id }]);
    setTimeout(() => setToasts(current => current.filter(t => t.id !== id)), 4600);
  }, []);

  const applySession = useCallback((auth: AuthResponse) => {
    tokenStore.set(auth.accessToken);
    setUser(auth.user);
    setOrganization(auth.organization ?? null);
    setOrganizations(auth.organizations);
  }, []);

  const refreshNotifications = useCallback(async () => {
    if (!tokenStore.get()) return;
    try {
      const result = await api.notifications();
      setNotifications(result.items);
      setUnread(result.unreadCount);
    } catch { /* the bell simply stays as it was */ }
  }, []);

  const signIn = useCallback(async (provider: string, idToken: string) => {
    applySession(await api.signIn(provider, idToken));
    invalidate();
  }, [applySession, invalidate]);

  const signOut = useCallback(() => {
    tokenStore.clear();
    hub.current?.stop();
    hub.current = null;
    setUser(null);
    setOrganization(null);
    setOrganizations([]);
    setNotifications([]);
    setUnread(0);
  }, []);

  const switchOrganization = useCallback(async (id: string) => {
    applySession(await api.switchOrganization(id));
    invalidate();
  }, [applySession, invalidate]);

  const refreshSession = useCallback(async () => {
    applySession(await api.me());
  }, [applySession]);

  const clearNotifications = useCallback(async (id?: string) => {
    // Optimistic: the list updates at once, and a failure brings the server's view back.
    setNotifications(current => (id ? current.filter(n => n.id !== id) : []));
    try {
      if (id) await api.clearNotification(id); else await api.clearNotifications();
    } catch {
      void refreshNotifications();
    }
  }, [refreshNotifications]);

  const markNotificationsRead = useCallback(async () => {
    setUnread(0);
    try { await api.markRead(); } catch { /* the badge clears locally regardless */ }
  }, []);

  // Restore an existing session on first load.
  useEffect(() => {
    (async () => {
      if (tokenStore.get()) {
        try { applySession(await api.me()); }
        catch { tokenStore.clear(); }
      }
      setReady(true);
    })();
  }, [applySession]);

  // Live channel: toasts, the bell and Processing-list progress arrive here
  // rather than being polled for.
  useEffect(() => {
    if (!organization) return;
    let cancelled = false;

    const connection = new HubConnectionBuilder()
      .withUrl(`${api.baseUrl}/hubs/notifications?access_token=${tokenStore.get() ?? ''}`)
      .withAutomaticReconnect()
      .build();

    connection.on('Notify', (payload: NotificationDto & { severity: string }) => {
      setNotifications(current => [{ ...payload, read: false }, ...current]);
      setUnread(count => count + 1);
      showToast({
        icon: payload.severity === 'Error' ? '!' : payload.severity === 'Warning' ? '!' : '✓',
        dot: payload.severity === 'Error' ? '#C2412D' : payload.severity === 'Warning' ? '#D08A1E' : '#2E9A64',
        title: payload.title,
        body: payload.body,
      });
      invalidate();
    });

    connection.on('SourceProgress', () => invalidate());

    connection.start().catch(() => {
      if (!cancelled) console.warn('Live notifications unavailable — falling back to polling.');
    });
    hub.current = connection;

    void refreshNotifications();

    return () => {
      cancelled = true;
      if (connection.state !== HubConnectionState.Disconnected) void connection.stop();
    };
  }, [organization, showToast, invalidate, refreshNotifications]);

  const value = useMemo<AppState>(() => ({
    ready, user, organization, organizations, notifications, unread, toasts, revision,
    signIn, signOut, switchOrganization, refreshSession, refreshNotifications,
    markNotificationsRead, clearNotifications, showToast, invalidate,
  }), [ready, user, organization, organizations, notifications, unread, toasts, revision,
    signIn, signOut, switchOrganization, refreshSession, refreshNotifications,
    markNotificationsRead, clearNotifications, showToast, invalidate]);

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useApp() {
  const value = useContext(Ctx);
  if (!value) throw new Error('useApp must be used inside <AppProvider>.');
  return value;
}
