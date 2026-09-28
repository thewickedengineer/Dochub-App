import { useCallback, useEffect, useState } from 'react';
import { useApp } from './AppContext';

/**
 * Fetches once per revision bump, so any mutation anywhere refreshes every
 * screen that depends on server state.
 */
export function useQuery<T>(fetcher: () => Promise<T>, deps: unknown[] = []) {
  const { revision, organization } = useApp();
  const [data, setData] = useState<T | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const run = useCallback(async () => {
    setError(null);
    try { setData(await fetcher()); }
    catch (e) { setError(e instanceof Error ? e.message : 'Something went wrong.'); }
    finally { setLoading(false); }
    // fetcher is recreated each render by design; the deps array drives reruns.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);

  useEffect(() => { void run(); }, [run, revision, organization?.id]);

  return { data, loading, error, reload: run };
}

export const formatBytes = (bytes: number) => {
  if (!bytes) return '—';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
};

export const formatRelative = (iso?: string) => {
  if (!iso) return '—';
  const then = new Date(iso).getTime();
  const seconds = Math.round((Date.now() - then) / 1000);
  if (seconds < 60) return 'Just now';
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
  if (seconds < 172800) return 'Yesterday';
  return new Date(iso).toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
};

/** Relative time that reads correctly in both directions, for next/last run. */
export const formatWhen = (iso?: string) => {
  if (!iso) return '—';
  const deltaSeconds = Math.round((new Date(iso).getTime() - Date.now()) / 1000);
  if (deltaSeconds <= 0) return formatRelative(iso);

  if (deltaSeconds < 60) return 'in under a minute';
  if (deltaSeconds < 3600) return `in ${Math.round(deltaSeconds / 60)}m`;
  if (deltaSeconds < 86400) return `in ${Math.round(deltaSeconds / 3600)}h`;

  const days = Math.round(deltaSeconds / 86400);
  if (days <= 14) return `in ${days} day${days === 1 ? '' : 's'}`;
  return new Date(iso).toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
};

export const formatTime = (iso?: string) =>
  iso ? new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : '';
