import { useState } from 'react';
import { api } from '../lib/api';
import { useApp } from '../lib/AppContext';
import { useQuery } from '../lib/hooks';
import { c } from '../theme';
import { Button, Empty, Input, PageHeading, Select } from '../components/ui';

export default function Members() {
  const { organization, user, showToast, invalidate } = useApp();
  const { data, loading } = useQuery(() => api.members(), []);
  const [email, setEmail] = useState('');
  const [role, setRole] = useState('Member');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const isOwner = user?.role === 'Owner';

  const add = async () => {
    if (!email.trim()) return;
    setBusy(true);
    setError(null);
    try {
      await api.addMember(email.trim(), role);
      showToast({
        icon: '✓', dot: '#2E9A64', title: 'Member added',
        body: `${email.trim()} can now sign in to ${organization?.name}.`,
      });
      setEmail('');
      invalidate();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not add that member.');
    } finally { setBusy(false); }
  };

  return (
    <>
      <PageHeading
        title="Members"
        subtitle={`Only the organization owner can add people to ${organization?.name ?? 'this organization'} and set their level.`}
      />

      <div style={{
        background: c.surface, border: `1px solid ${c.border}`, borderRadius: 12,
        padding: '16px 18px', display: 'flex', gap: 10, alignItems: 'center', flexWrap: 'wrap',
      }}>
        <Input
          value={email}
          disabled={!isOwner}
          placeholder="name@acme-insurance.com"
          onChange={e => setEmail(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter') void add(); }}
          style={{ flex: 1, minWidth: 220, height: 38, width: 'auto' }}
        />
        <Select
          value={role}
          disabled={!isOwner}
          onChange={e => setRole(e.target.value)}
          style={{ height: 38, width: 'auto' }}
        >
          <option>Member</option>
          <option>Admin</option>
        </Select>
        <Button variant="primary" disabled={!isOwner || busy || !email.trim()} onClick={() => void add()}>
          Add to organization
        </Button>
      </div>

      {(error || !isOwner) && (
        <div style={{ fontSize: 13, color: error ? '#B13A26' : c.dim, marginTop: -12 }}>
          {error ?? 'You can view the roster, but only an Owner can add members or change levels.'}
        </div>
      )}

      <div style={{ background: c.surface, border: `1px solid ${c.border}`, borderRadius: 12, overflow: 'hidden' }}>
        <div style={{
          display: 'grid', gridTemplateColumns: 'minmax(0,2fr) minmax(0,1.2fr) 120px 150px 110px',
          gap: 12, padding: '10px 18px', fontSize: 12, color: c.dim, fontWeight: 500,
          borderBottom: `1px solid ${c.rule}`, background: c.sidebar,
        }}>
          <span>Member</span><span>Teams</span><span>Sign-in</span><span>Level</span><span>Can create orgs</span>
        </div>

        {loading && <Empty>Loading…</Empty>}
        {!loading && (data?.length ?? 0) === 0 && <Empty>No members yet.</Empty>}

        {(data ?? []).map(m => (
          <div key={m.userId} style={{
            display: 'grid', gridTemplateColumns: 'minmax(0,2fr) minmax(0,1.2fr) 120px 150px 110px',
            gap: 12, padding: '12px 18px', fontSize: 14, alignItems: 'center',
            borderBottom: `1px solid ${c.ruleSoft}`,
          }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 10, minWidth: 0 }}>
              <span style={{
                width: 30, height: 30, borderRadius: '50%', background: '#E8E3D8',
                display: 'grid', placeItems: 'center', fontSize: 11, fontWeight: 600, flex: 'none',
              }}>{m.name.split(' ').filter(Boolean).slice(0, 2).map(w => w[0]).join('')}</span>
              <div style={{ minWidth: 0 }}>
                <div style={{ fontWeight: 500, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{m.name}</div>
                <div style={{ fontSize: 12, color: c.dim, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{m.email}</div>
              </div>
            </div>
            <span style={{ fontSize: 13, color: c.soft, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {m.teams.length > 0 ? m.teams.join(', ') : '—'}
            </span>
            <span style={{ fontSize: 13, color: c.muted, textTransform: 'capitalize' }}>
              {m.identityProvider === 'invited' ? 'Invited' : m.identityProvider}
            </span>
            <span style={{
              justifySelf: 'start', fontSize: 12, fontWeight: 500, border: `1px solid ${c.borderStrong}`,
              borderRadius: 7, padding: '4px 10px', color: c.ink,
            }}>{m.role}</span>
            <span style={{ fontSize: 13, color: c.muted }}>{m.canCreateOrganizations ? 'Yes' : 'No'}</span>
          </div>
        ))}
      </div>
    </>
  );
}
