import { useState } from 'react';
import { api } from '../lib/api';
import { useApp } from '../lib/AppContext';
import { formatRelative, useQuery } from '../lib/hooks';
import type { OwnerDto, PlatformOrganizationDto } from '../lib/types';
import { c } from '../theme';
import { Button, Card, Empty, Field, Input, Modal, PageHeading } from '../components/ui';

/**
 * The Creator's console: every organization on the platform and its owners.
 * A Creator sets organizations up for other people and does not need to be a
 * member of them. Owners are named by the address they sign in with.
 */
export default function Platform() {
  const { showToast, refreshSession } = useApp();
  const orgs = useQuery(() => api.platformOrganizations(), []);
  const [creating, setCreating] = useState(false);
  const [addingTo, setAddingTo] = useState<PlatformOrganizationDto | null>(null);

  const toast = (title: string, body: string, ok = true) =>
    showToast({ icon: ok ? '✓' : '!', dot: ok ? '#2E9A64' : '#C2412D', title, body });

  const removeOwner = async (org: PlatformOrganizationDto, owner: OwnerDto) => {
    try {
      await api.removeOwner(org.id, owner.userId);
      toast('Owner removed', `${owner.loginId} is now an Admin of ${org.name}.`);
      void orgs.reload();
    } catch (e) {
      toast('Could not remove owner', e instanceof Error ? e.message : 'Something went wrong.', false);
    }
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 20, maxWidth: 1000, width: '100%' }}>
      <PageHeading
        title="Organizations"
        subtitle="As a Creator you set up organizations and choose their owners. Owners then add everyone else."
        aside={<Button variant="primary" onClick={() => setCreating(true)}>+ Create organization</Button>}
      />

      {orgs.error && <Card><span style={{ color: '#B13A26', fontSize: 14 }}>{orgs.error}</span></Card>}
      {!orgs.loading && (orgs.data ?? []).length === 0 && (
        <Card><Empty>No organizations yet. Create the first and name its owner.</Empty></Card>
      )}

      {(orgs.data ?? []).map(org => (
        <Card key={org.id}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
            <div style={{
              width: 34, height: 34, borderRadius: 9, background: c.accent, color: '#fff',
              display: 'grid', placeItems: 'center', fontWeight: 600, fontSize: 13, flex: 'none',
            }}>{org.initials}</div>
            <div style={{ flex: 1, minWidth: 0 }}>
              <div style={{ fontWeight: 600, fontSize: 15 }}>{org.name}</div>
              <div style={{ fontSize: 12, color: c.dim }}>
                {org.memberCount} member{org.memberCount === 1 ? '' : 's'} · {org.plan} · created {formatRelative(org.createdAt)}
                {org.createdBy ? ` by ${org.createdBy}` : ''}
              </div>
            </div>
            <Button onClick={() => setAddingTo(org)} style={{ height: 34 }}>+ Add owner</Button>
          </div>

          <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
            <div style={{ fontSize: 12, fontWeight: 600, color: c.muted, textTransform: 'uppercase', letterSpacing: '.05em' }}>
              Owners
            </div>
            {org.owners.length === 0 && <div style={{ fontSize: 13, color: '#B13A26' }}>No owner — add one.</div>}
            {org.owners.map(owner => (
              <div key={owner.userId} style={{
                display: 'flex', alignItems: 'center', gap: 10, padding: '8px 10px',
                border: `1px solid ${c.border}`, borderRadius: 9,
              }}>
                <div style={{ flex: 1, minWidth: 0 }}>
                  <div style={{ fontSize: 14, fontWeight: 500 }}>{owner.displayName}</div>
                  <div style={{ fontSize: 12, color: c.dim }}>{owner.loginId}</div>
                </div>
                <span style={{
                  fontSize: 12, borderRadius: 99, padding: '2px 8px',
                  background: owner.hasSignedIn ? '#E7F4EC' : '#FBF1DF',
                  color: owner.hasSignedIn ? '#1F7A4F' : '#94600F',
                }}>{owner.hasSignedIn ? `Signed in · ${provider(owner.identityProvider)}` : 'Not signed in yet'}</span>
                <Button
                  variant="ghost"
                  disabled={org.owners.length <= 1}
                  onClick={() => void removeOwner(org, owner)}
                  style={{ height: 30, padding: '0 8px', fontSize: 13 }}
                >Remove</Button>
              </div>
            ))}
          </div>
        </Card>
      ))}

      {creating && (
        <OwnerForm
          title="Create organization"
          subtitle="Name the organization and the person who will own it."
          withName
          submitLabel="Create"
          onClose={() => setCreating(false)}
          onSubmit={async ({ name, loginId, displayName }) => {
            const created = await api.createOrganization(name, loginId, displayName);
            setCreating(false);
            toast('Organization created', `${created.name} — ${loginId || 'you'} ${loginId ? 'is' : 'are'} the owner.`);
            void orgs.reload();
            await refreshSession();
          }}
        />
      )}

      {addingTo && (
        <OwnerForm
          title={`Add an owner to ${addingTo.name}`}
          subtitle="They'll have full control of the organization, including adding members."
          submitLabel="Add owner"
          requireLogin
          onClose={() => setAddingTo(null)}
          onSubmit={async ({ loginId, displayName }) => {
            await api.addOwner(addingTo.id, loginId, displayName);
            toast('Owner added', `${loginId} now owns ${addingTo.name}.`);
            setAddingTo(null);
            void orgs.reload();
          }}
        />
      )}
    </div>
  );
}

function provider(value: string) {
  return { microsoft: 'Microsoft', google: 'Google', dev: 'dev sign-in' }[value] ?? value;
}

function OwnerForm({ title, subtitle, withName, requireLogin, submitLabel, onClose, onSubmit }: {
  title: string; subtitle: string; withName?: boolean; requireLogin?: boolean; submitLabel: string;
  onClose: () => void;
  onSubmit: (values: { name: string; loginId: string; displayName: string }) => Promise<void>;
}) {
  const [name, setName] = useState('');
  const [loginId, setLoginId] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const ready = (!withName || name.trim()) && (!requireLogin || loginId.trim());

  const submit = async () => {
    if (!ready || busy) return;
    setBusy(true);
    setError(null);
    try {
      await onSubmit({ name: name.trim(), loginId: loginId.trim(), displayName: displayName.trim() });
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Something went wrong.');
      setBusy(false);
    }
  };

  return (
    <Modal
      title={title}
      subtitle={subtitle}
      onClose={onClose}
      width={480}
      footer={<>
        <span style={{ flex: 1, fontSize: 13, color: '#B13A26' }}>{error}</span>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="primary" disabled={!ready || busy} onClick={() => void submit()}>{submitLabel}</Button>
      </>}
    >
      {withName && (
        <Field label="Organization name">
          <Input value={name} autoFocus placeholder="e.g. Contoso Insurance" onChange={e => setName(e.target.value)} />
        </Field>
      )}
      <Field label={withName ? 'Owner login id' : 'Login id'}>
        <Input
          value={loginId}
          autoFocus={!withName}
          type="email"
          placeholder="name@company.com"
          onChange={e => setLoginId(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter') void submit(); }}
        />
      </Field>
      <div style={{ fontSize: 12, color: c.dim, lineHeight: 1.5, marginTop: -4 }}>
        The exact address they sign in with — a Microsoft work or school account (name@company.com),
        a personal Microsoft account (name@outlook.com, or any address used as one), or a Google account.
        {withName ? ' Leave empty to own it yourself.' : ''} If they haven't signed in before, ownership
        is waiting for them when they first do.
      </div>
      <Field label="Display name (optional)">
        <Input value={displayName} placeholder="Shown until they sign in" onChange={e => setDisplayName(e.target.value)} />
      </Field>
    </Modal>
  );
}
