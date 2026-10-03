import { useState } from 'react';
import { api } from '../lib/api';
import { useApp } from '../lib/AppContext';
import { formatRelative, formatWhen, useQuery } from '../lib/hooks';
import { c, srcOf, stOf } from '../theme';
import type { SyncScheduleDto } from '../lib/types';
import { Button, Empty, SourceMark } from './ui';

/** Pill colours reuse the document-status palette so the page reads as one system. */
const statusTone = (status: string) =>
  status === 'Active' ? stOf('Indexed')
    : status === 'Paused' ? stOf('Skipped')
      : stOf('Failed');

export default function ScheduledUpdates() {
  const { showToast, invalidate } = useApp();
  const { data, loading } = useQuery(() => api.syncSchedules(), []);
  const [busyId, setBusyId] = useState<string | null>(null);

  const schedules = data ?? [];

  const act = async (id: string, work: () => Promise<string>) => {
    setBusyId(id);
    try {
      const message = await work();
      showToast({ icon: '✓', dot: '#2E9A64', title: 'Scheduled updates', body: message });
      invalidate();
    } catch (e) {
      showToast({
        icon: '!', dot: '#C2412D', title: 'Could not update the schedule',
        body: e instanceof Error ? e.message : 'Unknown error.',
      });
    } finally {
      setBusyId(null);
    }
  };

  const toggle = (s: SyncScheduleDto) => act(s.id, async () => {
    const next = s.status === 'Active' ? 'Paused' : 'Active';
    await api.updateSyncSchedule(s.id, { status: next });
    return `${s.artifactName} is now ${next.toLowerCase()}.`;
  });

  const runNow = (s: SyncScheduleDto) => act(s.id, async () => {
    const result = await api.runSyncSchedule(s.id);
    return result.message;
  });

  const remove = (s: SyncScheduleDto) => act(s.id, async () => {
    await api.deleteSyncSchedule(s.id);
    return `${s.artifactName} will no longer update on a schedule.`;
  });

  return (
    <>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 4, marginTop: 8 }}>
        <div style={{ fontSize: 18, fontWeight: 600, letterSpacing: '-0.01em' }}>Scheduled updates</div>
        <div style={{ color: c.muted, fontSize: 14 }}>
          SharePoint and Google Drive locations that re-check themselves. Files are matched
          by name, so a changed document refreshes its own vectors instead of duplicating.
        </div>
      </div>

      <div style={{ overflowX: 'auto' }}>
        <div style={{ minWidth: 640 }}>
          <div style={{ background: c.surface, border: `1px solid ${c.border}`, borderRadius: 12, overflow: 'hidden' }}>
            <div style={{
              display: 'grid', gridTemplateColumns: 'minmax(0,1.8fr) minmax(0,1.5fr) 130px minmax(0,1fr) 210px',
              gap: 12, padding: '10px 18px', fontSize: 12, color: c.dim, fontWeight: 500,
              borderBottom: `1px solid ${c.rule}`, background: c.sidebar,
            }}>
              <span>Artifact</span><span>Cadence</span><span>Next run</span><span>Last run</span><span />
            </div>

            {loading && <Empty>Loading…</Empty>}
            {!loading && schedules.length === 0 && (
              <Empty>
                Nothing on a schedule yet. Choose “Import and keep in sync” when adding a
                SharePoint, Google Drive or GitHub location.
              </Empty>
            )}

            {schedules.map(s => {
              const tone = statusTone(s.status);
              const busy = busyId === s.id;
              return (
                <div key={s.id} style={{
                  display: 'grid', gridTemplateColumns: 'minmax(0,1.8fr) minmax(0,1.5fr) 130px minmax(0,1fr) 210px',
                  gap: 12, padding: '13px 18px', fontSize: 14, alignItems: 'center',
                  borderBottom: `1px solid ${c.ruleSoft}`,
                  background: s.status === 'Failed' ? '#FEF8F6' : c.surface,
                }}>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 10, minWidth: 0 }}>
                    <SourceMark source={s.sourceType} size={24} />
                    <div style={{ minWidth: 0 }}>
                      <div style={{ fontWeight: 500, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                        {s.artifactName}
                      </div>
                      <div style={{ fontSize: 12, color: c.dim, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                        {s.path} · {srcOf(s.sourceType).label}
                      </div>
                    </div>
                  </div>

                  <div style={{ minWidth: 0 }}>
                    <div style={{ fontSize: 13, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                      {s.cadence}
                    </div>
                    <span style={{
                      display: 'inline-block', marginTop: 3, fontSize: 11, fontWeight: 500,
                      borderRadius: 99, padding: '2px 8px', background: tone.bg, color: tone.fg,
                    }}>{s.status}</span>
                  </div>

                  <span
                    title={s.status === 'Active' ? new Date(s.nextRunAt).toLocaleString() : undefined}
                    style={{ fontSize: 13, color: s.status === 'Active' ? c.soft : c.dim }}
                  >
                    {s.status === 'Active' ? formatWhen(s.nextRunAt) : '—'}
                  </span>

                  <div style={{ minWidth: 0 }}>
                    {s.lastRunAt ? (
                      <>
                        <div style={{ fontSize: 13 }}>{formatRelative(s.lastRunAt)}</div>
                        <div style={{ fontSize: 12, color: c.dim }}>
                          +{s.documentsAddedLastRun} added · {s.documentsUpdatedLastRun} updated · {s.documentsUnchangedLastRun} same
                        </div>
                      </>
                    ) : (
                      <span style={{ fontSize: 13, color: c.dim }}>Not run yet</span>
                    )}
                    {s.lastError && (
                      <div title={s.lastError} style={{
                        fontSize: 12, color: '#B13A26', overflow: 'hidden',
                        textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                      }}>{s.lastError}</div>
                    )}
                  </div>

                  <div style={{ display: 'flex', gap: 6, justifyContent: 'flex-end' }}>
                    <Button
                      style={{ height: 30, padding: '0 10px', fontSize: 12 }}
                      disabled={busy}
                      onClick={() => void runNow(s)}
                    >{busy ? '…' : 'Run now'}</Button>
                    <Button
                      style={{ height: 30, padding: '0 10px', fontSize: 12 }}
                      disabled={busy}
                      onClick={() => void toggle(s)}
                    >{s.status === 'Active' ? 'Pause' : 'Resume'}</Button>
                    <Button
                      style={{ height: 30, padding: '0 8px', fontSize: 12, color: '#B13A26' }}
                      disabled={busy}
                      onClick={() => void remove(s)}
                    >Stop</Button>
                  </div>
                </div>
              );
            })}
          </div>
        </div>
      </div>
    </>
  );
}
