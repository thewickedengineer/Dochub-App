import { useState, type ReactNode } from 'react';
import { NavLink, useLocation } from 'react-router-dom';
import { useApp } from '../lib/AppContext';
import { useQuery, formatTime } from '../lib/hooks';
import { api } from '../lib/api';
import { c } from '../theme';
import { Button, Empty, Field, Input, Modal } from './ui';

const NAV = [
  { key: '/upload', label: 'Upload', icon: '⇪' },
  { key: '/knowledge-base', label: 'Knowledge base', icon: '▤' },
  { key: '/workspace', label: 'Workspace', icon: '▦' },
  { key: '/members', label: 'Members', icon: '◎' },
  { key: '/connections', label: 'Connections', icon: '⇄' },
  { key: '/ask', label: 'Ask', icon: '✦' },
  { key: '/chat', label: 'Chat', icon: '💬' },
];

export default function Shell({ children }: { children: ReactNode }) {
  const { user, organization, organizations, unread, switchOrganization, signOut, showToast, invalidate } = useApp();
  const [orgMenuOpen, setOrgMenuOpen] = useState(false);
  const [notifOpen, setNotifOpen] = useState(false);
  const [createOrgOpen, setCreateOrgOpen] = useState(false);
  const location = useLocation();

  const kb = useQuery(() => api.knowledgeBase(), []);
  const sources = useQuery(() => api.sourceDocuments(20), []);
  const inFlight = (sources.data ?? []).filter(
    s => s.status === 'RequestUpload' || s.status === 'Uploading' || s.status === 'Processing').length;
  const coverage = kb.data?.stats.coveragePercent ?? 0;

  return (
    <div style={{ minHeight: '100vh', display: 'flex', flexDirection: 'column' }}>
      <header style={{
        height: 60, flex: 'none', background: c.surface, borderBottom: `1px solid ${c.border}`,
        display: 'flex', alignItems: 'center', gap: 12, padding: '0 24px',
        position: 'sticky', top: 0, zIndex: 25,
      }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
          <div style={{
            width: 30, height: 30, borderRadius: 8, background: c.ink, color: '#fff',
            display: 'grid', placeItems: 'center', fontWeight: 700, fontSize: 15,
          }}>D</div>
          <span style={{ fontWeight: 700, fontSize: 18, letterSpacing: '-0.02em', color: c.ink }}>Dochub</span>
        </div>
        <span style={{ flex: 1 }} />
        <button
          onClick={() => setNotifOpen(true)}
          title="Notifications"
          style={{
            position: 'relative', width: 38, height: 38, border: `1px solid ${c.border}`,
            borderRadius: 10, background: c.surface, cursor: 'pointer', display: 'grid', placeItems: 'center',
          }}
        >
          <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke={c.ink} strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
            <path d="M6 8a6 6 0 1 1 12 0c0 7 3 9 3 9H3s3-2 3-9" />
            <path d="M10.3 21a1.94 1.94 0 0 0 3.4 0" />
          </svg>
          {unread > 0 && (
            <span style={{
              position: 'absolute', top: -5, right: -5, minWidth: 18, height: 18, borderRadius: 99,
              background: '#C2412D', color: '#fff', fontSize: 11, fontWeight: 600,
              display: 'grid', placeItems: 'center', padding: '0 5px', border: '2px solid #fff',
            }}>{unread}</span>
          )}
        </button>
      </header>

      <div style={{ flex: 1, display: 'grid', gridTemplateColumns: '248px minmax(0,1fr)' }}>
        <aside style={{
          background: c.sidebar, borderRight: `1px solid ${c.border}`, display: 'flex',
          flexDirection: 'column', position: 'sticky', top: 60, height: 'calc(100vh - 60px)',
        }}>
          <div style={{ padding: '16px 14px 10px', position: 'relative' }}>
            <button
              onClick={() => setOrgMenuOpen(v => !v)}
              style={{
                width: '100%', display: 'flex', alignItems: 'center', gap: 10, padding: 8,
                border: '1px solid transparent', borderRadius: 10, background: 'transparent',
                cursor: 'pointer', textAlign: 'left', fontFamily: 'inherit',
              }}
            >
              <div style={{
                width: 30, height: 30, borderRadius: 8, background: c.accent, color: '#fff',
                display: 'grid', placeItems: 'center', fontWeight: 600, fontSize: 13, flex: 'none',
              }}>{organization?.initials ?? '—'}</div>
              <div style={{ flex: 1, minWidth: 0 }}>
                <div style={{ fontWeight: 600, fontSize: 14, color: c.ink, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {organization?.name ?? 'No organization'}
                </div>
                <div style={{ fontSize: 12, color: c.dim }}>
                  {organization ? `${user?.role ?? 'Member'} · ${organization.plan}` : 'Ask an owner for access'}
                </div>
              </div>
              <span style={{ color: c.dim, fontSize: 11 }}>▼</span>
            </button>

            {orgMenuOpen && (
              <div style={{
                position: 'absolute', left: 14, right: 14, top: 62, background: c.surface,
                border: `1px solid ${c.border}`, borderRadius: 12,
                boxShadow: '0 12px 32px rgba(22,24,29,.12)', padding: 6, zIndex: 30,
                animation: 'slideUp .15s ease',
              }}>
                <div style={{
                  fontSize: 11, fontWeight: 600, color: c.dim, letterSpacing: '.06em',
                  textTransform: 'uppercase', padding: '8px 10px 4px',
                }}>Organizations</div>
                {organizations.map(o => (
                  <button
                    key={o.id}
                    onClick={() => { setOrgMenuOpen(false); void switchOrganization(o.id); }}
                    style={{
                      width: '100%', display: 'flex', alignItems: 'center', gap: 10, padding: '8px 10px',
                      border: 0, background: 'transparent', borderRadius: 8, cursor: 'pointer',
                      fontSize: 14, color: c.ink, textAlign: 'left', fontFamily: 'inherit',
                    }}
                  >
                    <span style={{
                      width: 22, height: 22, borderRadius: 6, background: '#EEF0F3',
                      display: 'grid', placeItems: 'center', fontSize: 11, fontWeight: 600,
                    }}>{o.initials}</span>
                    <span style={{ flex: 1 }}>{o.name}</span>
                    <span style={{ color: c.accent }}>{o.id === organization?.id ? '✓' : ''}</span>
                  </button>
                ))}
                <div style={{ height: 1, background: c.rule, margin: '6px 4px' }} />
                <button
                  onClick={() => { setOrgMenuOpen(false); setCreateOrgOpen(true); }}
                  disabled={!user?.canCreateOrganizations}
                  style={{
                    width: '100%', padding: '8px 10px', border: 0, background: 'transparent',
                    borderRadius: 8, cursor: user?.canCreateOrganizations ? 'pointer' : 'not-allowed',
                    fontSize: 14, color: user?.canCreateOrganizations ? c.accent : c.disabled,
                    textAlign: 'left', fontWeight: 500, fontFamily: 'inherit',
                  }}
                >+ Create organization</button>
                <div style={{ fontSize: 12, color: c.dim, padding: '2px 10px 8px' }}>
                  Available to Owner and Admin levels
                </div>
              </div>
            )}
          </div>

          <nav style={{ padding: '6px 14px', display: 'flex', flexDirection: 'column', gap: 2 }}>
            {[...NAV, ...(user?.isCreator ? [{ key: '/platform', label: 'Organizations', icon: '⌂' }] : [])].map(n => {
              const on = location.pathname.startsWith(n.key);
              return (
                <NavLink
                  key={n.key}
                  to={n.key}
                  style={{
                    display: 'flex', alignItems: 'center', gap: 10, height: 36, padding: '0 10px',
                    borderRadius: 8, fontSize: 14, textDecoration: 'none',
                    background: on ? c.active : 'transparent',
                    color: on ? c.ink : '#4B5160', fontWeight: on ? 600 : 500,
                  }}
                >
                  <span style={{ width: 18, textAlign: 'center', fontSize: 15, opacity: .8 }}>{n.icon}</span>
                  <span style={{ flex: 1 }}>{n.label}</span>
                  {n.key === '/upload' && inFlight > 0 && (
                    <span style={{
                      fontSize: 11, fontWeight: 600, background: c.accent, color: '#fff',
                      borderRadius: 99, padding: '1px 7px',
                    }}>{inFlight}</span>
                  )}
                </NavLink>
              );
            })}
          </nav>

          <div style={{
            marginTop: 'auto', padding: 14, borderTop: `1px solid ${c.border}`,
            display: 'flex', flexDirection: 'column', gap: 10,
          }}>
            <div style={{
              display: 'flex', flexDirection: 'column', gap: 6, padding: 12, borderRadius: 10,
              background: c.surface, border: `1px solid ${c.border}`,
            }}>
              <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 12, color: c.muted }}>
                <span>Index coverage</span>
                <span style={{ fontFamily: "'Geist Mono', monospace", color: c.ink }}>{coverage}%</span>
              </div>
              <div style={{ height: 6, borderRadius: 99, background: c.track, overflow: 'hidden' }}>
                <div style={{ height: '100%', background: c.accent, width: `${coverage}%`, transition: 'width .4s ease' }} />
              </div>
              <div style={{ fontSize: 12, color: c.dim }}>
                {kb.data?.stats.indexed ?? 0} of {kb.data?.stats.totalDocuments ?? 0} documents
              </div>
            </div>

            <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
              <div style={{
                width: 30, height: 30, borderRadius: '50%', background: '#E8E3D8',
                display: 'grid', placeItems: 'center', fontSize: 12, fontWeight: 600, flex: 'none',
              }}>{initials(user?.displayName)}</div>
              <div style={{ flex: 1, minWidth: 0 }}>
                <div style={{ fontSize: 13, fontWeight: 500, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {user?.displayName}
                </div>
                <div style={{ fontSize: 12, color: c.dim, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {user?.email}
                </div>
              </div>
              <button
                onClick={signOut}
                style={{ border: 0, background: 'transparent', color: c.dim, cursor: 'pointer', fontSize: 12, fontFamily: 'inherit' }}
              >Sign out</button>
            </div>
          </div>
        </aside>

        <div style={{ display: 'flex', flexDirection: 'column', minWidth: 0 }}>
          <main style={{ padding: '28px 32px 48px', display: 'flex', flexDirection: 'column', gap: 24, minWidth: 0 }}>
            {children}
          </main>
        </div>
      </div>

      {notifOpen && <NotificationDrawer onClose={() => setNotifOpen(false)} />}

      {createOrgOpen && (
        <CreateOrganizationModal
          onClose={() => setCreateOrgOpen(false)}
          onCreated={name => {
            setCreateOrgOpen(false);
            showToast({ icon: '✓', dot: '#2E9A64', title: 'Organization created', body: name });
            invalidate();
          }}
        />
      )}

      <Toasts />
    </div>
  );
}

function NotificationDrawer({ onClose }: { onClose: () => void }) {
  const { notifications, markNotificationsRead, clearNotifications } = useApp();

  // Opening the drawer is the read signal, matching the design's badge behaviour.
  useState(() => { void markNotificationsRead(); return null; });

  return (
    <>
      <div onClick={onClose} style={{ position: 'fixed', inset: 0, background: 'rgba(22,24,29,.12)', zIndex: 40 }} />
      <div style={{
        position: 'fixed', top: 0, right: 0, bottom: 0, width: 380, background: c.surface,
        borderLeft: `1px solid ${c.border}`, boxShadow: '-12px 0 40px rgba(22,24,29,.1)',
        zIndex: 41, display: 'flex', flexDirection: 'column',
      }}>
        <div style={{ height: 60, display: 'flex', alignItems: 'center', padding: '0 20px', borderBottom: `1px solid ${c.rule}` }}>
          <span style={{ flex: 1, fontWeight: 600, fontSize: 15 }}>Notifications</span>
          {notifications.length > 0 && (
            <button
              onClick={() => void clearNotifications()}
              style={{ border: 0, background: 'transparent', color: c.accent, cursor: 'pointer', fontSize: 13, fontFamily: 'inherit', marginRight: 10 }}
            >Clear all</button>
          )}
          <button onClick={onClose} style={{ border: 0, background: 'transparent', fontSize: 18, cursor: 'pointer', color: c.muted }}>×</button>
        </div>
        <div style={{ flex: 1, overflow: 'auto', display: 'flex', flexDirection: 'column' }}>
          {notifications.length === 0 && <Empty>You're all caught up.</Empty>}
          {notifications.map(n => {
            const tone = n.severity === 'Error'
              ? { bg: '#FBEAE6', fg: '#B13A26', icon: '!' }
              : n.severity === 'Warning'
                ? { bg: '#FBF1DF', fg: '#94600F', icon: '!' }
                : n.severity === 'Success'
                  ? { bg: '#E7F4EC', fg: '#1F7A4F', icon: '✓' }
                  : { bg: '#E9EEFC', fg: c.accent, icon: '↻' };
            return (
              <div key={n.id} style={{ display: 'flex', gap: 12, padding: '14px 20px', borderBottom: `1px solid ${c.ruleSoft}` }}>
                <span style={{
                  width: 28, height: 28, borderRadius: '50%', display: 'grid', placeItems: 'center',
                  fontSize: 13, flex: 'none', background: tone.bg, color: tone.fg,
                }}>{tone.icon}</span>
                <div style={{ display: 'flex', flexDirection: 'column', gap: 3, minWidth: 0, flex: 1 }}>
                  <div style={{ fontSize: 14, fontWeight: 500 }}>{n.title}</div>
                  <div style={{ fontSize: 13, color: c.muted, lineHeight: 1.45 }}>{n.body}</div>
                  <div style={{ fontSize: 12, color: c.faint }}>{formatTime(n.createdAt)}</div>
                </div>
                <button
                  title="Clear this notification"
                  aria-label="Clear this notification"
                  onClick={() => void clearNotifications(n.id)}
                  style={{ border: 0, background: 'transparent', color: c.faint, cursor: 'pointer', fontSize: 16, alignSelf: 'flex-start', padding: 2 }}
                >×</button>
              </div>
            );
          })}
        </div>
      </div>
    </>
  );
}

function CreateOrganizationModal({ onClose, onCreated }: { onClose: () => void; onCreated: (name: string) => void }) {
  const [name, setName] = useState('');
  const [owner, setOwner] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const submit = async () => {
    if (!name.trim()) return;
    setBusy(true);
    setError(null);
    try {
      const created = await api.createOrganization(name.trim(), owner.trim() || undefined);
      onCreated(created.name);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not create the organization.');
      setBusy(false);
    }
  };

  return (
    <Modal
      title="Create organization"
      subtitle="Name its owner, or leave the owner empty to own it yourself."
      onClose={onClose}
      footer={<>
        <span style={{ flex: 1, fontSize: 13, color: '#B13A26' }}>{error}</span>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="primary" disabled={busy || !name.trim()} onClick={() => void submit()}>Create</Button>
      </>}
    >
      <Field label="Name">
        <Input
          value={name}
          autoFocus
          placeholder="e.g. Acme Life"
          onChange={e => setName(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter') void submit(); }}
        />
      </Field>
      <Field label="Owner login id (optional)">
        <Input
          value={owner}
          type="email"
          placeholder="name@company.com — the address they sign in with"
          onChange={e => setOwner(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter') void submit(); }}
        />
      </Field>
      <div style={{ fontSize: 12, color: c.dim, marginTop: -4 }}>
        A Microsoft work, school or personal account, or a Google account. If you name someone else,
        you won't be added to the organization.
      </div>
    </Modal>
  );
}

function Toasts() {
  const { toasts } = useApp();
  return (
    <div style={{
      position: 'fixed', bottom: 24, right: 24, zIndex: 60,
      display: 'flex', flexDirection: 'column', gap: 10, alignItems: 'flex-end',
    }}>
      {toasts.map(t => (
        <div key={t.id} style={{
          background: c.ink, color: '#fff', borderRadius: 12, padding: '14px 16px',
          display: 'flex', gap: 12, alignItems: 'flex-start', maxWidth: 380,
          boxShadow: '0 12px 32px rgba(22,24,29,.3)', animation: 'slideUp .2s ease',
        }}>
          <span style={{
            width: 22, height: 22, borderRadius: '50%', background: t.dot,
            display: 'grid', placeItems: 'center', fontSize: 12, flex: 'none',
          }}>{t.icon}</span>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 3 }}>
            <div style={{ fontSize: 14, fontWeight: 600 }}>{t.title}</div>
            <div style={{ fontSize: 13, color: '#B9BDC6', lineHeight: 1.45 }}>{t.body}</div>
          </div>
        </div>
      ))}
    </div>
  );
}

const initials = (name?: string) =>
  (name ?? '?').split(' ').filter(Boolean).slice(0, 2).map(w => w[0].toUpperCase()).join('');
