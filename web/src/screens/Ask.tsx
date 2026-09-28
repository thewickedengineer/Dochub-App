import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { api } from '../lib/api';
import { useApp } from '../lib/AppContext';
import { useQuery } from '../lib/hooks';
import { c } from '../theme';
import { Button, Card, SourceMark } from '../components/ui';

const SUGGESTIONS = [
  'What is our SLA for acknowledging first notice of loss?',
  'How does the rating engine apply territory factors?',
  'Who approves commercial referrals over $5M?',
];

/**
 * Retrieval is owned by the indexing service that consumes the Service Bus
 * queue, so this screen shows the corpus the answer would be drawn from and
 * states plainly that the query endpoint is not wired up yet.
 */
export default function Ask() {
  const { user } = useApp();
  const navigate = useNavigate();
  const [question, setQuestion] = useState('');
  const [asked, setAsked] = useState('');

  const kb = useQuery(() => api.knowledgeBase(), []);
  const teams = useQuery(() => api.teams(), []);

  const stats = kb.data?.stats;
  const recent = (kb.data?.artifacts ?? [])
    .filter(a => a.indexedDocuments > 0)
    .slice(0, 5);

  return (
    <div style={{ maxWidth: 880, width: '100%', margin: '0 auto', display: 'flex', flexDirection: 'column', gap: 24, paddingTop: 24 }}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        <div style={{ fontSize: 30, fontWeight: 600, letterSpacing: '-0.025em' }}>
          {greeting()}, {user?.displayName.split(' ')[0] ?? 'there'}
        </div>
        <div style={{ color: c.muted, fontSize: 15 }}>
          Answers are grounded in {stats?.indexed ?? 0} indexed documents across {stats?.teams ?? 0} teams.
        </div>
      </div>

      <div style={{
        background: c.surface, border: `1px solid ${c.borderStrong}`, borderRadius: 14,
        boxShadow: '0 1px 2px rgba(22,24,29,.04), 0 8px 24px rgba(22,24,29,.05)',
        padding: '14px 16px', display: 'flex', flexDirection: 'column', gap: 12,
      }}>
        <input
          value={question}
          onChange={e => setQuestion(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter') setAsked(question || SUGGESTIONS[0]); }}
          placeholder="Ask a question about policies, claims, systems…"
          style={{
            border: 0, outline: 0, fontSize: 17, padding: '6px 2px',
            color: c.ink, background: 'transparent', fontFamily: 'inherit',
          }}
        />
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
          <span style={{ fontSize: 13, color: c.muted, border: `1px solid ${c.border}`, borderRadius: 99, padding: '4px 10px' }}>
            Scope: All teams
          </span>
          <span style={{ fontSize: 13, color: c.muted, border: `1px solid ${c.border}`, borderRadius: 99, padding: '4px 10px' }}>
            Any source
          </span>
          <span style={{ flex: 1 }} />
          <Button variant="primary" style={{ height: 34, padding: '0 16px' }} onClick={() => setAsked(question || SUGGESTIONS[0])}>
            Ask ↵
          </Button>
        </div>
      </div>

      {asked ? (
        <div style={{
          background: c.surface, border: `1px solid ${c.border}`, borderRadius: 14,
          padding: '22px 24px', display: 'flex', flexDirection: 'column', gap: 16,
          animation: 'slideUp .2s ease',
        }}>
          <div style={{ fontSize: 18, fontWeight: 600, letterSpacing: '-0.01em' }}>{asked}</div>
          <div style={{
            fontSize: 15, lineHeight: 1.65, color: c.body,
            background: '#FBF1DF', borderRadius: 10, padding: '14px 16px',
          }}>
            Retrieval is not connected yet. The API stores and indexes documents and publishes
            a <code style={{ fontFamily: "'Geist Mono', monospace", fontSize: 13 }}>dochub.processing.requested</code> event
            per job; a retrieval service that consumes that queue and writes back embeddings
            will make this screen answer from {stats?.indexed ?? 0} indexed documents.
          </div>
          <div style={{ display: 'flex', gap: 8 }}>
            <Button style={{ height: 32, padding: '0 12px', fontSize: 13 }} onClick={() => { setAsked(''); setQuestion(''); }}>
              New question
            </Button>
            <Button style={{ height: 32, padding: '0 12px', fontSize: 13 }} onClick={() => navigate('/knowledge-base')}>
              View the index
            </Button>
          </div>
        </div>
      ) : (
        <>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
            {SUGGESTIONS.map(s => (
              <button
                key={s}
                onClick={() => { setQuestion(s); setAsked(s); }}
                style={{
                  border: `1px solid ${c.border}`, background: c.surface, borderRadius: 99,
                  padding: '7px 14px', fontSize: 13, color: c.soft, cursor: 'pointer', fontFamily: 'inherit',
                }}
              >{s}</button>
            ))}
          </div>

          <div style={{ display: 'grid', gridTemplateColumns: 'minmax(0,1fr) minmax(0,1fr)', gap: 16 }}>
            <Card>
              <div style={{ fontWeight: 600, fontSize: 14 }}>Recently indexed</div>
              {recent.length === 0 && <div style={{ fontSize: 13, color: c.dim }}>Nothing indexed yet.</div>}
              {recent.map(a => (
                <div key={a.id} style={{ display: 'flex', alignItems: 'center', gap: 10, fontSize: 14, minWidth: 0 }}>
                  <SourceMark source={a.primarySource} size={24} />
                  <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                    {a.name}
                  </span>
                  <span style={{ fontSize: 12, color: c.dim }}>{a.indexedDocuments} docs</span>
                </div>
              ))}
            </Card>

            <Card>
              <div style={{ fontWeight: 600, fontSize: 14 }}>Your teams</div>
              {(teams.data ?? []).length === 0 && <div style={{ fontSize: 13, color: c.dim }}>No teams yet.</div>}
              {(teams.data ?? []).map(t => (
                <div
                  key={t.id}
                  onClick={() => navigate('/workspace')}
                  style={{ display: 'flex', alignItems: 'center', gap: 10, fontSize: 14, cursor: 'pointer' }}
                >
                  <span style={{
                    width: 24, height: 24, borderRadius: 6, background: '#EEF0F3',
                    display: 'grid', placeItems: 'center', fontSize: 11, fontWeight: 600, flex: 'none',
                  }}>{t.name[0]}</span>
                  <span style={{ flex: 1 }}>{t.name}</span>
                  <span style={{ fontSize: 12, color: c.dim }}>
                    {t.groupCount} groups · {t.artifactCount} artifacts
                  </span>
                </div>
              ))}
            </Card>
          </div>
        </>
      )}
    </div>
  );
}

function greeting() {
  const hour = new Date().getHours();
  if (hour < 12) return 'Good morning';
  if (hour < 18) return 'Good afternoon';
  return 'Good evening';
}
