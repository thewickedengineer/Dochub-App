import { Fragment, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { api } from '../lib/api';
import { useApp } from '../lib/AppContext';
import { formatRelative, useQuery } from '../lib/hooks';
import type { ChatConversationDto, ChatEvent, ChatMessageDto, ChatScope, ChatSource, TeamDto } from '../lib/types';
import { c, mono } from '../theme';
import { Button } from '../components/ui';

interface ScopeOption { value: string; scope: ChatScope; scopeId?: string; label: string; depth: number }

const ORG_SCOPE = 'Organization:';

function scopeOptions(teams: TeamDto[], orgName: string): ScopeOption[] {
  const options: ScopeOption[] = [{ value: ORG_SCOPE, scope: 'Organization', label: `All of ${orgName}`, depth: 0 }];
  for (const team of teams) {
    options.push({ value: `Team:${team.id}`, scope: 'Team', scopeId: team.id, label: team.name, depth: 0 });
    for (const group of team.groups) {
      options.push({ value: `Group:${group.id}`, scope: 'Group', scopeId: group.id, label: group.name, depth: 1 });
      for (const artifact of group.artifacts) {
        options.push({ value: `Artifact:${artifact.id}`, scope: 'Artifact', scopeId: artifact.id, label: artifact.name, depth: 2 });
      }
    }
  }
  return options;
}

/** A message as shown: saved ones from the server, plus the one streaming in. */
interface Turn extends ChatMessageDto { streaming?: boolean; query?: string }

export default function Chat() {
  const { organization, showToast: toast } = useApp();
  const showToast = useMemo(() => (body: string) =>
    toast({ icon: '!', dot: '#C2412D', title: 'Chat', body }), [toast]);
  const { id } = useParams();
  const [search, setSearch] = useSearchParams();
  const navigate = useNavigate();

  const conversations = useQuery(() => api.conversations(), []);
  const teams = useQuery(() => api.teams(), []);
  const options = useMemo(() => scopeOptions(teams.data ?? [], organization?.name ?? 'the organization'),
    [teams.data, organization?.name]);

  const [active, setActive] = useState<ChatConversationDto | null>(null);
  const [turns, setTurns] = useState<Turn[]>([]);
  const [draftScope, setDraftScope] = useState(ORG_SCOPE);
  const [input, setInput] = useState('');
  const [busy, setBusy] = useState(false);
  const abort = useRef<AbortController | null>(null);
  const bottom = useRef<HTMLDivElement>(null);
  const autoAsked = useRef(false);
  // The conversation this screen just created mid-send: its URL change must not reload or abort it.
  const created = useRef<string | null>(null);

  // Load the conversation named in the URL.
  useEffect(() => {
    if (id && id === created.current) { created.current = null; return; }
    abort.current?.abort();
    if (!id) { setActive(null); setTurns([]); return; }
    let live = true;
    api.conversation(id)
      .then(detail => { if (live) { setActive(detail.conversation); setTurns(detail.messages); } })
      .catch(() => { if (live) { showToast('That conversation no longer exists.'); navigate('/chat', { replace: true }); } });
    return () => { live = false; };
  }, [id, navigate, showToast]);

  useEffect(() => { bottom.current?.scrollIntoView({ behavior: 'smooth', block: 'end' }); }, [turns]);

  // Arriving from the Ask screen with a question: start a conversation and send it.
  useEffect(() => {
    const q = search.get('q');
    if (q && !id && !autoAsked.current) {
      autoAsked.current = true;
      setSearch({}, { replace: true });
      void send(q);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [search, id]);

  async function send(text: string) {
    const question = text.trim();
    if (!question || busy) return;
    setBusy(true);
    setInput('');

    let conversation = active;
    try {
      if (!conversation) {
        const option = options.find(o => o.value === draftScope) ?? options[0];
        conversation = await api.createConversation(option.scope, option.scopeId);
        created.current = conversation.id;
        setActive(conversation);
        navigate(`/chat/${conversation.id}`, { replace: true });
      }
    } catch (e) {
      showToast(e instanceof Error ? e.message : 'Could not start the conversation.');
      setBusy(false);
      return;
    }

    const now = new Date().toISOString();
    const pendingId = `pending-${Date.now()}`;
    setTurns(t => [...t,
      { id: `${pendingId}-q`, role: 'User', content: question, createdAt: now },
      { id: pendingId, role: 'Assistant', content: '', createdAt: now, streaming: true }]);

    const update = (patch: (turn: Turn) => Turn) =>
      setTurns(t => t.map(turn => (turn.id === pendingId ? patch(turn) : turn)));

    const controller = new AbortController();
    abort.current = controller;
    try {
      await api.ask(conversation.id, question, (event: ChatEvent) => {
        switch (event.type) {
          case 'started':
            setActive(a => (a ? { ...a, title: event.title } : a));
            break;
          case 'sources':
            update(turn => ({ ...turn, sources: event.sources, query: event.query }));
            break;
          case 'delta':
            update(turn => ({ ...turn, content: turn.content + event.text }));
            break;
          case 'done':
            update(turn => ({ ...turn, cited: event.cited, model: event.model, streaming: false }));
            break;
          case 'error':
            update(turn => ({ ...turn, error: event.detail ? `${event.error}: ${event.detail}` : event.error, streaming: false }));
            break;
        }
      }, controller.signal);
    } catch (e) {
      if (!controller.signal.aborted) {
        update(turn => ({ ...turn, error: e instanceof Error ? e.message : 'The answer failed.', streaming: false }));
      }
    } finally {
      update(turn => ({ ...turn, streaming: false }));
      setBusy(false);
      void conversations.reload();
    }
  }

  async function changeScope(value: string) {
    if (!active) { setDraftScope(value); return; }
    const option = options.find(o => o.value === value);
    if (!option) return;
    try {
      setActive(await api.updateConversation(active.id, { scope: option.scope, scopeId: option.scopeId }));
      void conversations.reload();
    } catch (e) {
      showToast(e instanceof Error ? e.message : 'Could not change the scope.');
    }
  }

  async function remove(conversation: ChatConversationDto) {
    try {
      await api.deleteConversation(conversation.id);
      if (conversation.id === id) navigate('/chat');
      void conversations.reload();
    } catch (e) {
      showToast(e instanceof Error ? e.message : 'Could not delete the conversation.');
    }
  }

  const scopeValue = active ? (active.scope === 'Organization' ? ORG_SCOPE : `${active.scope}:${active.scopeId}`) : draftScope;

  return (
    <div style={{
      display: 'grid', gridTemplateColumns: '260px minmax(0,1fr)', gap: 0, margin: '-28px -32px',
      height: 'calc(100vh - 60px)', minHeight: 0,
    }}>
      {/* ── Conversations ─────────────────────────────────────────────── */}
      <aside style={{
        borderRight: `1px solid ${c.border}`, background: c.sidebar, display: 'flex',
        flexDirection: 'column', minHeight: 0,
      }}>
        <div style={{ padding: 14 }}>
          <Button variant="primary" style={{ width: '100%' }} onClick={() => { abort.current?.abort(); navigate('/chat'); }}>
            + New chat
          </Button>
        </div>
        <div style={{ flex: 1, overflow: 'auto', padding: '0 8px 12px', display: 'flex', flexDirection: 'column', gap: 2 }}>
          {(conversations.data ?? []).length === 0 && (
            <div style={{ fontSize: 13, color: c.dim, padding: '8px 10px' }}>No conversations yet.</div>
          )}
          {(conversations.data ?? []).map(conv => (
            <ConversationRow key={conv.id} conversation={conv} active={conv.id === id}
              onOpen={() => navigate(`/chat/${conv.id}`)} onDelete={() => void remove(conv)} />
          ))}
        </div>
      </aside>

      {/* ── Thread ────────────────────────────────────────────────────── */}
      <section style={{ display: 'flex', flexDirection: 'column', minHeight: 0, background: c.canvas }}>
        <div style={{
          padding: '14px 24px', borderBottom: `1px solid ${c.border}`, background: c.surface,
          display: 'flex', alignItems: 'center', gap: 12,
        }}>
          <div style={{ flex: 1, minWidth: 0, fontWeight: 600, fontSize: 15, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {active?.title ?? 'New chat'}
          </div>
          <span style={{ fontSize: 13, color: c.muted }}>Searching</span>
          <select
            value={scopeValue}
            onChange={e => void changeScope(e.target.value)}
            disabled={busy}
            aria-label="What to search"
            style={{
              height: 34, border: `1px solid ${c.borderStrong}`, borderRadius: 8, padding: '0 8px',
              fontSize: 13, background: c.surface, color: c.ink, fontFamily: 'inherit', maxWidth: 320,
            }}
          >
            {options.map(o => (
              <option key={o.value} value={o.value}>
                {'  '.repeat(o.depth)}{o.scope === 'Organization' ? '' : `${o.scope}: `}{o.label}
              </option>
            ))}
          </select>
        </div>

        <div style={{ flex: 1, overflow: 'auto', padding: '24px 24px 8px' }}>
          <div style={{ maxWidth: 820, margin: '0 auto', display: 'flex', flexDirection: 'column', gap: 18 }}>
            {turns.length === 0 && <Welcome onPick={q => void send(q)} />}
            {turns.map(turn => turn.role === 'User'
              ? <Question key={turn.id} text={turn.content} />
              : <Answer key={turn.id} turn={turn} />)}
            <div ref={bottom} />
          </div>
        </div>

        <form
          onSubmit={e => { e.preventDefault(); void send(input); }}
          style={{ padding: '12px 24px 20px' }}
        >
          <div style={{
            maxWidth: 820, margin: '0 auto', background: c.surface, border: `1px solid ${c.borderStrong}`,
            borderRadius: 14, padding: '10px 12px', display: 'flex', alignItems: 'flex-end', gap: 10,
            boxShadow: '0 1px 2px rgba(22,24,29,.04), 0 8px 24px rgba(22,24,29,.05)',
          }}>
            <textarea
              value={input}
              onChange={e => setInput(e.target.value)}
              onKeyDown={e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); void send(input); } }}
              placeholder="Ask about your documents…  (Shift+Enter for a new line)"
              rows={Math.min(6, Math.max(1, input.split('\n').length))}
              style={{
                flex: 1, border: 0, outline: 0, resize: 'none', fontSize: 15, lineHeight: 1.5,
                fontFamily: 'inherit', color: c.ink, background: 'transparent', padding: '4px 2px',
              }}
            />
            {busy
              ? <Button onClick={() => abort.current?.abort()} style={{ height: 34 }}>Stop</Button>
              : <Button type="submit" variant="accent" disabled={!input.trim()} style={{ height: 34 }}>Send ↵</Button>}
          </div>
          <div style={{ maxWidth: 820, margin: '6px auto 0', fontSize: 12, color: c.dim }}>
            Answers come only from documents indexed in this organization, with numbered sources.
          </div>
        </form>
      </section>
    </div>
  );
}

function ConversationRow({ conversation, active, onOpen, onDelete }: {
  conversation: ChatConversationDto; active: boolean; onOpen: () => void; onDelete: () => void;
}) {
  const [hover, setHover] = useState(false);
  return (
    <div
      onClick={onOpen}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        display: 'flex', alignItems: 'center', gap: 6, padding: '8px 10px', borderRadius: 8, cursor: 'pointer',
        background: active ? c.active : hover ? c.hover : 'transparent',
      }}
    >
      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{ fontSize: 13, fontWeight: active ? 600 : 500, color: c.ink, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {conversation.title}
        </div>
        <div style={{ fontSize: 11, color: c.dim, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {conversation.scopeLabel} · {formatRelative(conversation.updatedAt)}
        </div>
      </div>
      {hover && (
        <button
          title="Delete conversation"
          onClick={e => { e.stopPropagation(); onDelete(); }}
          style={{ border: 0, background: 'transparent', color: c.dim, cursor: 'pointer', fontSize: 15 }}
        >×</button>
      )}
    </div>
  );
}

const STARTERS = [
  'What is our SLA for acknowledging first notice of loss?',
  'Summarise the settlement authority limits by grade.',
  'What did the caller report on the FNOL call?',
];

function Welcome({ onPick }: { onPick: (q: string) => void }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 14, padding: '40px 0 10px', alignItems: 'center', textAlign: 'center' }}>
      <div style={{ fontSize: 26, fontWeight: 600, letterSpacing: '-0.02em' }}>Chat with your documents</div>
      <div style={{ color: c.muted, fontSize: 14, maxWidth: 520 }}>
        Pick what to search above — the whole organization, a team, a group or one artifact — and ask.
        Every answer cites the passages it came from.
      </div>
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8, justifyContent: 'center' }}>
        {STARTERS.map(s => (
          <button key={s} onClick={() => onPick(s)} style={{
            border: `1px solid ${c.border}`, background: c.surface, borderRadius: 99, padding: '7px 14px',
            fontSize: 13, color: c.soft, cursor: 'pointer', fontFamily: 'inherit',
          }}>{s}</button>
        ))}
      </div>
    </div>
  );
}

function Question({ text }: { text: string }) {
  return (
    <div style={{ alignSelf: 'flex-end', maxWidth: '80%', background: c.ink, color: '#fff', borderRadius: '14px 14px 4px 14px',
      padding: '10px 14px', fontSize: 15, lineHeight: 1.55, whiteSpace: 'pre-wrap' }}>
      {text}
    </div>
  );
}

function Answer({ turn }: { turn: Turn }) {
  const [focus, setFocus] = useState<number | null>(null);
  const [showAll, setShowAll] = useState(false);
  const sources = turn.sources ?? [];
  const cited = new Set(turn.cited ?? []);
  const shown = showAll || cited.size === 0 ? sources : sources.filter(s => cited.has(s.n));

  return (
    <div style={{
      background: c.surface, border: `1px solid ${c.border}`, borderRadius: '14px 14px 14px 4px',
      padding: '14px 18px', display: 'flex', flexDirection: 'column', gap: 12, maxWidth: '92%',
    }}>
      {turn.content
        ? <div style={{ fontSize: 15, lineHeight: 1.65, color: c.body }}>{render(turn.content, n => setFocus(n))}</div>
        : turn.streaming && (
          <div style={{ fontSize: 14, color: c.dim }}>
            {sources.length ? `Reading ${sources.length} passages…` : 'Searching your documents…'}
          </div>
        )}
      {turn.streaming && turn.content && <span style={{ color: c.dim, fontSize: 12 }}>▍</span>}

      {turn.error && (
        <div style={{ fontSize: 13, color: '#B13A26', background: '#FBEAE6', borderRadius: 8, padding: '8px 10px' }}>
          {turn.error}
        </div>
      )}

      {sources.length > 0 && !turn.streaming && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 6, borderTop: `1px solid ${c.rule}`, paddingTop: 10 }}>
          <div style={{ fontSize: 12, fontWeight: 600, color: c.muted, textTransform: 'uppercase', letterSpacing: '.05em' }}>
            Sources
          </div>
          {shown.map(source => (
            <SourceRow key={source.chunk_id} source={source} open={focus === source.n}
              onToggle={() => setFocus(f => (f === source.n ? null : source.n))} />
          ))}
          {cited.size > 0 && sources.length > shown.length && (
            <button onClick={() => setShowAll(true)} style={{
              alignSelf: 'flex-start', border: 0, background: 'transparent', color: c.accent, fontSize: 13,
              cursor: 'pointer', padding: 0, fontFamily: 'inherit',
            }}>Show {sources.length - shown.length} more passages that were searched</button>
          )}
        </div>
      )}
      {turn.model && !turn.streaming && (
        <div style={{ fontSize: 11, color: c.faint }}>Answered by {turn.model}</div>
      )}
    </div>
  );
}

function SourceRow({ source, open, onToggle }: { source: ChatSource; open: boolean; onToggle: () => void }) {
  // A sheet's name is already in its location; don't say it twice.
  const section = source.chunk_type === 'sheet_rows' ? '' : source.heading_path?.join(' › ');
  const where = [section, source.location].filter(Boolean).join(' · ');
  return (
    <div style={{ border: `1px solid ${open ? c.accent : c.border}`, borderRadius: 9, background: open ? c.accentTint : c.surface }}>
      <button onClick={onToggle} style={{
        width: '100%', display: 'flex', alignItems: 'center', gap: 10, padding: '7px 10px', border: 0,
        background: 'transparent', cursor: 'pointer', textAlign: 'left', fontFamily: 'inherit',
      }}>
        <span style={{
          minWidth: 22, height: 22, borderRadius: 6, background: c.accent, color: '#fff', fontSize: 12,
          fontWeight: 600, display: 'grid', placeItems: 'center',
        }}>{source.n}</span>
        <span style={{ fontSize: 13, fontWeight: 500, color: c.ink, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {source.filename ?? source.title ?? 'Document'}
        </span>
        {where && <span style={{ fontSize: 12, color: c.dim, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', flex: 1 }}>{where}</span>}
        <span style={{ color: c.dim, fontSize: 11, marginLeft: 'auto' }}>{open ? '▲' : '▼'}</span>
      </button>
      {open && (
        <div style={{
          padding: '0 12px 10px 42px', fontSize: 13, lineHeight: 1.55, color: c.soft, whiteSpace: 'pre-wrap',
          fontFamily: ['code_symbol', 'table', 'sheet_rows'].includes(source.chunk_type) ? mono : 'inherit',
        }}>{source.snippet}{source.snippet.length >= 600 ? '…' : ''}</div>
      )}
    </div>
  );
}

// ── A small, safe renderer for answers: paragraphs, lists, **bold**, `code`, [n] ──

const INLINE = /(\*\*[^*]+\*\*|`[^`]+`|\[\d{1,2}\])/g;

function inline(text: string, onCite: (n: number) => void): ReactNode[] {
  return text.split(INLINE).map((part, i) => {
    if (/^\[\d{1,2}\]$/.test(part)) {
      const n = Number(part.slice(1, -1));
      return (
        <button key={i} onClick={() => onCite(n)} title={`Source ${n}`} style={{
          border: 0, background: '#E9EEFC', color: c.accent, borderRadius: 5, fontSize: 11, fontWeight: 600,
          padding: '1px 5px', margin: '0 1px', cursor: 'pointer', verticalAlign: 'text-top', fontFamily: 'inherit',
        }}>{n}</button>
      );
    }
    if (part.startsWith('**') && part.endsWith('**')) return <strong key={i}>{part.slice(2, -2)}</strong>;
    if (part.startsWith('`') && part.endsWith('`')) {
      return <code key={i} style={{ fontFamily: mono, fontSize: 13, background: c.ruleSoft, borderRadius: 4, padding: '1px 4px' }}>{part.slice(1, -1)}</code>;
    }
    return <Fragment key={i}>{part}</Fragment>;
  });
}

function render(text: string, onCite: (n: number) => void): ReactNode {
  return text.split(/\n{2,}/).map((block, i) => {
    const lines = block.split('\n').filter(l => l.trim());
    const bullet = /^\s*(?:[-*•]|\d+[.)])\s+/;
    if (lines.length > 0 && lines.every(l => bullet.test(l))) {
      const ordered = /^\s*\d/.test(lines[0]);
      const items = lines.map((l, j) => <li key={j} style={{ margin: '2px 0' }}>{inline(l.replace(bullet, ''), onCite)}</li>);
      return ordered
        ? <ol key={i} style={{ margin: '6px 0', paddingLeft: 22 }}>{items}</ol>
        : <ul key={i} style={{ margin: '6px 0', paddingLeft: 22 }}>{items}</ul>;
    }
    return <p key={i} style={{ margin: '6px 0', whiteSpace: 'pre-wrap' }}>{inline(block, onCite)}</p>;
  });
}
