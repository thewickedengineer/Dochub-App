import { useState } from 'react';
import { useApp } from '../lib/AppContext';
import { c } from '../theme';
import { Button, Input } from '../components/ui';

const GOOGLE_CLIENT_ID = import.meta.env.VITE_GOOGLE_CLIENT_ID as string | undefined;
const MICROSOFT_CLIENT_ID = import.meta.env.VITE_MICROSOFT_CLIENT_ID as string | undefined;
const ALLOW_DEV = import.meta.env.VITE_ALLOW_DEV_SIGNIN === 'true';

export default function SignIn() {
  const { signIn } = useApp();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [devEmail, setDevEmail] = useState('');

  const run = async (provider: string, token: string) => {
    setBusy(true);
    setError(null);
    try { await signIn(provider, token); }
    catch (e) { setError(e instanceof Error ? e.message : 'Sign-in failed.'); }
    finally { setBusy(false); }
  };

  const ssoUnavailable = (provider: string) =>
    setError(`${provider} sign-in is not configured yet. Set the client id in web/.env and register the OAuth app.`);

  return (
    <div style={{ minHeight: '100vh', display: 'grid', gridTemplateColumns: 'minmax(0,1.1fr) minmax(0,1fr)' }}>
      <div style={{
        background: c.ink, color: '#fff', padding: '48px 56px',
        display: 'flex', flexDirection: 'column', justifyContent: 'space-between',
      }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
          <div style={{
            width: 28, height: 28, borderRadius: 7, background: '#fff', color: c.ink,
            display: 'grid', placeItems: 'center', fontWeight: 700, fontSize: 15,
          }}>D</div>
          <span style={{ fontWeight: 700, fontSize: 17, letterSpacing: '-0.02em' }}>Dochub</span>
        </div>
        <div style={{ maxWidth: 520, display: 'flex', flexDirection: 'column', gap: 18 }}>
          <div style={{ fontSize: 44, lineHeight: 1.08, fontWeight: 600, letterSpacing: '-0.03em' }}>
            Every answer your organization already knows.
          </div>
          <div style={{ fontSize: 17, lineHeight: 1.55, color: '#A9ADB6' }}>
            Connect GitHub, SharePoint, Google Drive and local files. Organize them by team,
            index them once, and ask in plain language with cited sources.
          </div>
        </div>
        <div style={{ fontSize: 13, color: '#6F737C' }}>
          SOC 2 Type II · Permissions inherited from source systems
        </div>
      </div>

      <div style={{ display: 'grid', placeItems: 'center', padding: 48, background: '#fff' }}>
        <div style={{ width: '100%', maxWidth: 380, display: 'flex', flexDirection: 'column', gap: 22 }}>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
            <div style={{ fontSize: 26, fontWeight: 600, letterSpacing: '-0.02em' }}>Sign in to Dochub</div>
            <div style={{ color: c.muted, fontSize: 15 }}>
              Use your work account. New users are created automatically.
            </div>
          </div>

          <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            <SsoButton
              disabled={busy}
              onClick={() => GOOGLE_CLIENT_ID ? startGoogle(run, setError) : ssoUnavailable('Google')}
              icon={<span style={{
                width: 18, height: 18, borderRadius: '50%',
                border: '4px solid #4285F4', borderRightColor: '#34A853',
                borderBottomColor: '#FBBC05', borderLeftColor: '#EA4335',
              }} />}
            >Continue with Google</SsoButton>

            <SsoButton
              disabled={busy}
              onClick={() => MICROSOFT_CLIENT_ID ? startMicrosoft(run, setError) : ssoUnavailable('Microsoft')}
              icon={<span style={{ display: 'grid', gridTemplateColumns: '8px 8px', gap: 2 }}>
                <span style={{ height: 8, background: '#F25022' }} />
                <span style={{ height: 8, background: '#7FBA00' }} />
                <span style={{ height: 8, background: '#00A4EF' }} />
                <span style={{ height: 8, background: '#FFB900' }} />
              </span>}
            >Continue with Microsoft</SsoButton>
          </div>

          {error && (
            <div style={{
              fontSize: 13, color: '#B13A26', background: '#FBEAE6',
              borderRadius: 9, padding: '10px 12px', lineHeight: 1.45,
            }}>{error}</div>
          )}

          {ALLOW_DEV && (
            <div style={{
              display: 'flex', flexDirection: 'column', gap: 8, borderTop: `1px solid ${c.rule}`, paddingTop: 18,
            }}>
              <div style={{ fontSize: 12, fontWeight: 600, color: c.dim, letterSpacing: '.06em', textTransform: 'uppercase' }}>
                Local development
              </div>
              <Input
                value={devEmail}
                onChange={e => setDevEmail(e.target.value)}
                placeholder="you@example.com"
                onKeyDown={e => { if (e.key === 'Enter') void run('dev', devEmail); }}
              />
              <Button variant="primary" disabled={busy} onClick={() => void run('dev', devEmail)}>
                Sign in without SSO
              </Button>
              <div style={{ fontSize: 12, color: c.dim, lineHeight: 1.5 }}>
                Available only while the API has Sso:AllowDevSignIn enabled.
              </div>
            </div>
          )}

          <div style={{
            fontSize: 13, color: c.dim, lineHeight: 1.5,
            borderTop: `1px solid ${c.rule}`, paddingTop: 18,
          }}>
            You'll get access to an organization once its owner adds you. Signing in with
            Microsoft or Google also lets you connect SharePoint or Drive later.
          </div>
        </div>
      </div>
    </div>
  );
}

function SsoButton({ children, icon, onClick, disabled }: {
  children: React.ReactNode; icon: React.ReactNode; onClick: () => void; disabled?: boolean;
}) {
  return (
    <button
      onClick={onClick}
      disabled={disabled}
      style={{
        height: 46, border: `1px solid ${c.borderStrong}`, background: '#fff', borderRadius: 10,
        display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 10,
        fontSize: 15, fontWeight: 500, cursor: disabled ? 'wait' : 'pointer',
        color: c.ink, fontFamily: 'inherit',
      }}
    >{icon}{children}</button>
  );
}

/**
 * Google Identity Services issues the ID token in the browser; the API verifies
 * it server-side before trusting any of its claims.
 */
function startGoogle(run: (p: string, t: string) => void, onError: (m: string) => void) {
  const google = (window as unknown as { google?: any }).google;
  if (!google?.accounts?.id) {
    onError('The Google Identity script has not loaded yet. Check your network and retry.');
    return;
  }
  google.accounts.id.initialize({
    client_id: GOOGLE_CLIENT_ID,
    callback: (response: { credential: string }) => run('google', response.credential),
  });
  google.accounts.id.prompt();
}

/**
 * Microsoft sign-in returns to the same /oauth/callback the source connections
 * use. One callback path means one redirect URI to register: having two nearly
 * identical ones is the kind of detail that costs an afternoon when only one of
 * them is in the portal.
 */
function startMicrosoft(_run: (p: string, t: string) => void, onError: (m: string) => void) {
  if (!MICROSOFT_CLIENT_ID) { onError('Microsoft client id is not configured.'); return; }
  const tenant = import.meta.env.VITE_MICROSOFT_TENANT ?? 'common';
  const nonce = crypto.randomUUID();
  sessionStorage.setItem('dochub.nonce', nonce);
  const params = new URLSearchParams({
    client_id: MICROSOFT_CLIENT_ID,
    response_type: 'id_token',
    redirect_uri: `${window.location.origin}/oauth/callback`,
    scope: 'openid profile email',
    response_mode: 'fragment',
    nonce,
  });
  window.location.href = `https://login.microsoftonline.com/${tenant}/oauth2/v2.0/authorize?${params}`;
}
