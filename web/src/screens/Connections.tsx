import { useState } from 'react';
import { api } from '../lib/api';
import { connectWithProvider } from '../lib/oauth';
import { useApp } from '../lib/AppContext';
import { formatRelative, useQuery } from '../lib/hooks';
import type { ConnectionOptionDto, SourceConnectionDto } from '../lib/types';
import { c } from '../theme';
import { Button, Card, Input, PageHeading, SourceMark } from '../components/ui';
import RemoveConnection from '../components/RemoveConnection';

const SIGN_IN_LABEL: Record<string, string> = {
  GitHub: 'Sign in with GitHub',
  SharePoint: 'Sign in with Microsoft',
  AzureDevOps: 'Sign in with Microsoft',
  GoogleDrive: 'Sign in with Google',
};

/**
 * The accounts Dochub reads documents through, for the signed-in user in this
 * organization. Each can be connected, switched to another account, or removed.
 */
export default function Connections() {
  const { showToast, invalidate } = useApp();
  const options = useQuery(() => api.connectionOptions(), []);
  const connections = useQuery(() => api.connections(), []);
  const [removing, setRemoving] = useState<SourceConnectionDto | null>(null);

  const toast = (title: string, body: string, ok = true) =>
    showToast({ icon: ok ? '✓' : '!', dot: ok ? '#2E9A64' : '#C2412D', title, body });

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 20, maxWidth: 1000, width: '100%' }}>
      <PageHeading
        title="Connections"
        subtitle="The accounts Dochub reads your documents through. Only you can use your connections, and only in this organization."
      />

      {(options.error || connections.error) && (
        <Card><span style={{ color: '#B13A26', fontSize: 14 }}>{options.error ?? connections.error}</span></Card>
      )}

      {(options.data ?? []).map(option => (
        <ConnectionCard
          key={option.sourceType}
          option={option}
          connection={(connections.data ?? []).find(x => x.sourceType === option.sourceType && x.status !== 'Revoked')}
          onChanged={(title, body) => { toast(title, body); invalidate(); }}
          onRemove={setRemoving}
        />
      ))}

      {removing && (
        <RemoveConnection
          connection={removing}
          onClose={() => setRemoving(null)}
          onRemoved={() => {
            toast(`${removing.displayName} removed`, 'Dochub no longer holds access to this account.');
            setRemoving(null);
            invalidate();
          }}
        />
      )}
    </div>
  );
}

function ConnectionCard({ option, connection, onChanged, onRemove }: {
  option: ConnectionOptionDto;
  connection?: SourceConnectionDto;
  onChanged: (title: string, body: string) => void;
  onRemove: (connection: SourceConnectionDto) => void;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [token, setToken] = useState('');
  const [pasting, setPasting] = useState(false);

  const connected = connection?.status === 'Connected';
  const lapsed = connection && !connected;
  const canSignIn = option.signIn && option.signInConfigured;
  const showToken = option.acceptsToken && pasting;

  const signIn = async () => {
    setBusy(true);
    setError(null);
    try {
      const created = await connectWithProvider(option.sourceType);
      onChanged(`${option.label} connected`, created.account ? `Signed in as ${created.account}.` : 'Access granted.');
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not connect.');
    } finally {
      setBusy(false);
    }
  };

  const saveToken = async () => {
    if (!token.trim()) return;
    setBusy(true);
    setError(null);
    try {
      const created = await api.connect(option.sourceType, token.trim());
      setToken('');
      setPasting(false);
      onChanged(`${option.label} connected`, created.account ? `Reading as ${created.account}.` : 'Token stored.');
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not connect.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Card>
      <div style={{ display: 'flex', alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
        <SourceMark source={option.sourceType} size={34} />
        <div style={{ flex: 1, minWidth: 200 }}>
          <div style={{ fontWeight: 600, fontSize: 15 }}>{option.label}</div>
          <div style={{ fontSize: 12, color: c.dim }}>
            {connected
              ? <>Connected{connection.account ? ` as ${connection.account}` : ''} · since {formatRelative(connection.updatedAt)}
                  {connection.scheduleCount > 0 ? ` · ${connection.scheduleCount} scheduled sync${connection.scheduleCount === 1 ? '' : 's'}` : ''}</>
              : lapsed
                ? <span style={{ color: '#94600F' }}>Access has {connection.status === 'Expired' ? 'expired' : 'stopped working'} — connect again.</span>
                : option.sourceType === 'GitHub'
                  ? 'Not connected. Public repositories work without it; private ones need it.'
                  : 'Not connected.'}
          </div>
        </div>

        <span style={{
          fontSize: 12, borderRadius: 99, padding: '2px 8px',
          background: connected ? '#E7F4EC' : '#FBF1DF', color: connected ? '#1F7A4F' : '#94600F',
        }}>{connected ? 'Connected' : lapsed ? 'Needs reconnecting' : 'Not connected'}</span>

        {option.signIn && (
          <Button variant={connected ? undefined : 'primary'} disabled={busy || !canSignIn} onClick={() => void signIn()} style={{ height: 34 }}>
            {busy ? 'Waiting for sign-in…' : connected ? 'Switch account' : SIGN_IN_LABEL[option.sourceType] ?? 'Sign in'}
          </Button>
        )}
        {option.acceptsToken && !pasting && (
          <Button variant="ghost" onClick={() => setPasting(true)} style={{ height: 34, color: c.dim, fontSize: 13 }}>Advanced: use a token</Button>
        )}
        {connection && (
          <Button variant="ghost" disabled={busy} onClick={() => onRemove(connection)} style={{ height: 34, color: '#B13A26' }}>
            Remove
          </Button>
        )}
      </div>

      {option.signIn && !option.signInConfigured && (
        <div style={{ fontSize: 12, color: '#94600F' }}>
          Sign-in for {option.label} isn't set up on this Dochub yet: an administrator registers the
          {option.sourceType === 'GitHub' ? ' GitHub OAuth App' : option.sourceType === 'GoogleDrive' ? ' Google OAuth client' : ' app'} first.
        </div>
      )}

      {showToken && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
          {option.sourceType === 'GitHub' && (
            <div style={{ fontSize: 12, color: c.dim, lineHeight: 1.5 }}>
              A{' '}
              <a href="https://github.com/settings/personal-access-tokens/new" target="_blank" rel="noreferrer" style={{ color: c.accent }}>
                fine-grained personal access token
              </a>{' '}
              with read-only <i>Contents</i> access to the repositories you need.
            </div>
          )}
          <div style={{ display: 'flex', gap: 8 }}>
            <Input
              value={token}
              type="password"
              placeholder={option.sourceType === 'GitHub' ? 'github_pat_…' : 'Paste the access token'}
              onChange={e => setToken(e.target.value)}
              onKeyDown={e => { if (e.key === 'Enter') void saveToken(); }}
            />
            <Button disabled={busy || !token.trim()} onClick={() => void saveToken()}>
              {busy ? 'Checking…' : connected ? 'Replace' : 'Connect'}
            </Button>
            {pasting && <Button variant="ghost" onClick={() => { setPasting(false); setToken(''); }}>Cancel</Button>}
          </div>
        </div>
      )}

      {error && <div style={{ fontSize: 13, color: '#B13A26' }}>{error}</div>}
    </Card>
  );
}
