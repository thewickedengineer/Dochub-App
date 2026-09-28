import { useState } from 'react';
import { api } from '../lib/api';
import { useApp } from '../lib/AppContext';
import { formatRelative, useQuery } from '../lib/hooks';
import { c, stOf } from '../theme';
import { Bar, Button, Empty, SourceMark, StatusPill } from './ui';

/**
 * Every source that has been submitted, and where it has got to. The source row
 * carries the whole journey, so one line answers "what happened to my upload".
 */
export default function ProcessingList() {
  const { showToast, invalidate } = useApp();
  const { data, loading } = useQuery(() => api.sourceDocuments(50), []);
  const [busyId, setBusyId] = useState<string | null>(null);

  const sources = data ?? [];

  const retry = async (id: string) => {
    setBusyId(id);
    try {
      const result = await api.retrySourceDocument(id);
      showToast({ icon: '↻', dot: c.accent, title: 'Back on the queue', body: result.message });
      invalidate();
    } catch (e) {
      showToast({
        icon: '!', dot: '#C2412D', title: 'Could not retry',
        body: e instanceof Error ? e.message : 'Unknown error.',
      });
    } finally { setBusyId(null); }
  };

  return (
    <>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 4, marginTop: 8 }}>
        <div style={{ fontSize: 18, fontWeight: 600, letterSpacing: '-0.01em' }}>Processing</div>
        <div style={{ color: c.muted, fontSize: 14 }}>
          Every Process request lands here and moves through Request Upload → Uploading →
          Uploaded → Processing → Processed. You'll be notified as each one progresses.
        </div>
      </div>

      <div style={{ overflowX: 'auto' }}>
        <div style={{ minWidth: 700 }}>
          <div style={{ background: c.surface, border: `1px solid ${c.border}`, borderRadius: 12, overflow: 'hidden' }}>
            <div style={{
              display: 'grid', gridTemplateColumns: '120px minmax(0,2fr) 56px minmax(140px,1fr) 160px 80px',
              gap: 12, padding: '10px 18px', fontSize: 12, color: c.dim, fontWeight: 500,
              borderBottom: `1px solid ${c.rule}`, background: c.sidebar,
            }}>
              <span>Source</span><span>Artifact</span>
              <span style={{ textAlign: 'right' }}>Docs</span>
              <span>Progress</span><span>Status</span><span />
            </div>

            {loading && <Empty>Loading…</Empty>}
            {!loading && sources.length === 0 && (
              <Empty>Nothing submitted yet. Add a source above, then press Process.</Empty>
            )}

            {sources.map(s => {
              const tone = stOf(s.statusLabel);
              const inFlight = s.status === 'Uploading' || s.status === 'Processing' || s.status === 'RequestUpload';
              const failed = s.status === 'Failed' || s.status === 'PartiallyFailed';
              return (
                <div key={s.id} style={{
                  display: 'grid', gridTemplateColumns: '120px minmax(0,2fr) 56px minmax(140px,1fr) 160px 80px',
                  gap: 12, padding: '13px 18px', fontSize: 14, alignItems: 'center',
                  borderBottom: `1px solid ${c.ruleSoft}`,
                  background: failed ? '#FEF8F6' : inFlight ? '#FAFBFF' : c.surface,
                }}>
                  <div>
                    <div style={{ fontFamily: "'Geist Mono', monospace", fontSize: 13, color: c.soft }}>
                      {s.reference}
                    </div>
                    <div style={{ fontSize: 12, color: c.dim }}>{formatRelative(s.requestedAt)}</div>
                  </div>

                  <div style={{ display: 'flex', alignItems: 'center', gap: 10, minWidth: 0 }}>
                    <SourceMark source={s.sourceType} size={24} />
                    <div style={{ minWidth: 0 }}>
                      <div style={{ fontWeight: 500, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                        {s.artifactName}
                        {s.isScheduled && (
                          <span
                            title="Submitted by a recurring schedule"
                            style={{
                              marginLeft: 8, fontSize: 11, fontWeight: 500, borderRadius: 99,
                              padding: '1px 7px', background: c.track, color: c.muted,
                            }}
                          >scheduled</span>
                        )}
                      </div>
                      <div
                        title={s.error ?? s.sourceReference}
                        style={{ fontSize: 12, color: failed ? '#B13A26' : c.dim, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}
                      >{s.error ?? `${s.path} · ${s.requestedBy}`}</div>
                    </div>
                  </div>

                  <span style={{ textAlign: 'right', fontVariantNumeric: 'tabular-nums' }}>
                    {s.totalDocuments}
                  </span>

                  <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                    <Bar percent={s.progressPercent} color={failed ? tone.dot : inFlight ? c.accent : tone.dot} />
                    <span style={{ fontSize: 12, color: c.muted, fontFamily: "'Geist Mono', monospace", width: 36, textAlign: 'right' }}>
                      {s.progressPercent}%
                    </span>
                  </div>

                  <StatusPill status={s.statusLabel} />

                  <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
                    {failed && (
                      <Button
                        style={{ height: 28, padding: '0 10px', fontSize: 12 }}
                        disabled={busyId === s.id}
                        onClick={() => void retry(s.id)}
                      >{busyId === s.id ? '…' : 'Retry'}</Button>
                    )}
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
