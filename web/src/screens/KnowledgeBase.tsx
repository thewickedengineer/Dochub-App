import { useNavigate } from 'react-router-dom';
import { api } from '../lib/api';
import { useApp } from '../lib/AppContext';
import { formatRelative, useQuery } from '../lib/hooks';
import { c, srcOf, stOf } from '../theme';
import { Bar, Card, Empty, PageHeading, SourceMark, StatusPill } from '../components/ui';

export default function KnowledgeBase() {
  const { organization } = useApp();
  const navigate = useNavigate();
  const { data, loading } = useQuery(() => api.knowledgeBase(), []);

  const stats = data?.stats;
  const cards = [
    { label: 'Indexed documents', value: stats?.indexed ?? 0, dot: stOf('Indexed').dot },
    { label: 'Pending', value: stats?.pending ?? 0, dot: stOf('Pending').dot },
    { label: 'Processing', value: stats?.processing ?? 0, dot: stOf('Processing').dot },
    { label: 'Failed', value: stats?.failed ?? 0, dot: stOf('Failed').dot },
  ];

  return (
    <>
      <PageHeading
        title="Knowledge base"
        subtitle={`What's currently in ${organization?.name ?? 'your organization'}'s index. Visible to every member.`}
        aside={<span style={{ fontSize: 13, color: c.muted }}>
          Last indexed {data?.lastIndexedAt ? formatRelative(data.lastIndexedAt) : '—'}
        </span>}
      />

      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4,minmax(0,1fr))', gap: 14 }}>
        {cards.map(card => (
          <Card key={card.label} pad={0} style={{ padding: '16px 18px', gap: 6 }}>
            <div style={{ fontSize: 13, color: c.muted, display: 'flex', alignItems: 'center', gap: 6 }}>
              <span style={{ width: 8, height: 8, borderRadius: '50%', background: card.dot }} />
              {card.label}
            </div>
            <div style={{ fontSize: 28, fontWeight: 600, letterSpacing: '-0.02em', fontVariantNumeric: 'tabular-nums' }}>
              {card.value}
            </div>
          </Card>
        ))}
      </div>

      <div style={{ display: 'grid', gridTemplateColumns: 'minmax(0,2.2fr) minmax(0,1fr)', gap: 14, alignItems: 'start' }}>
        <div style={{ background: c.surface, border: `1px solid ${c.border}`, borderRadius: 12, overflow: 'hidden' }}>
          <div style={{ padding: '14px 18px', borderBottom: `1px solid ${c.rule}`, fontWeight: 600, fontSize: 14 }}>
            Artifacts
          </div>
          <div style={{
            display: 'grid', gridTemplateColumns: 'minmax(0,2fr) minmax(0,1.2fr) 70px 150px 110px',
            gap: 12, padding: '10px 18px', fontSize: 12, color: c.dim, fontWeight: 500,
            borderBottom: `1px solid ${c.rule}`, background: c.sidebar,
          }}>
            <span>Artifact</span><span>Source</span>
            <span style={{ textAlign: 'right' }}>Docs</span>
            <span>Indexed</span><span>Status</span>
          </div>

          {loading && <Empty>Loading…</Empty>}
          {!loading && (data?.artifacts.length ?? 0) === 0 && (
            <Empty>No artifacts yet. Create one in Workspace, then upload documents to it.</Empty>
          )}

          {(data?.artifacts ?? []).map(a => {
            const percent = a.totalDocuments === 0 ? 0 : Math.round(a.indexedDocuments / a.totalDocuments * 100);
            return (
              <div
                key={a.id}
                onClick={() => navigate(`/workspace?artifact=${a.id}`)}
                style={{
                  display: 'grid', gridTemplateColumns: 'minmax(0,2fr) minmax(0,1.2fr) 70px 150px 110px',
                  gap: 12, padding: '12px 18px', fontSize: 14, alignItems: 'center',
                  borderBottom: `1px solid ${c.ruleSoft}`, cursor: 'pointer',
                }}
              >
                <div style={{ minWidth: 0 }}>
                  <div style={{ fontWeight: 500, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{a.name}</div>
                  <div style={{ fontSize: 12, color: c.dim }}>{a.teamName} › {a.groupName}</div>
                </div>
                <div style={{ display: 'flex', alignItems: 'center', gap: 8, minWidth: 0 }}>
                  <SourceMark source={a.primarySource} />
                  <span style={{ fontSize: 13, color: c.soft, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                    {srcOf(a.primarySource).label}
                  </span>
                </div>
                <span style={{ textAlign: 'right', fontVariantNumeric: 'tabular-nums' }}>{a.totalDocuments}</span>
                <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                  <Bar percent={percent} />
                  <span style={{ fontSize: 12, color: c.muted, fontFamily: "'Geist Mono', monospace", width: 36, textAlign: 'right' }}>
                    {percent}%
                  </span>
                </div>
                <StatusPill status={a.status} />
              </div>
            );
          })}
        </div>

        <Card>
          <div style={{ fontWeight: 600, fontSize: 14 }}>Documents by source</div>
          {(data?.bySource.length ?? 0) === 0 && (
            <div style={{ fontSize: 13, color: c.dim }}>Nothing uploaded yet.</div>
          )}
          {(data?.bySource ?? []).map(b => (
            <div key={b.sourceType} style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: 13 }}>
                <SourceMark source={b.sourceType} size={20} />
                <span style={{ flex: 1 }}>{b.label}</span>
                <span style={{ fontFamily: "'Geist Mono', monospace", color: c.muted }}>{b.count}</span>
              </div>
              <div style={{ height: 6, borderRadius: 99, background: c.track, overflow: 'hidden' }}>
                <div style={{ height: '100%', background: c.ink, width: `${b.percent}%` }} />
              </div>
            </div>
          ))}
        </Card>
      </div>
    </>
  );
}
