import { useEffect, useState } from 'react';
import { api } from '../lib/api';
import { c } from '../theme';

/**
 * Where the provider sends the browser back. It runs inside the popup: it
 * exchanges the code through the API, reports the outcome to the window that
 * opened it, and closes.
 */
export default function OAuthCallback() {
  const [message, setMessage] = useState('Finishing sign-in…');

  useEffect(() => {
    const params = new URLSearchParams(window.location.search);
    const code = params.get('code');
    const state = params.get('state');
    // The provider reports a refusal here rather than by failing the redirect.
    const providerError = params.get('error_description') ?? params.get('error');

    const report = (payload: { connection?: unknown; error?: string }) => {
      window.opener?.postMessage({ type: 'dochub:oauth', ...payload }, window.location.origin);
      // Give the opener a tick to receive it before the window goes away.
      setTimeout(() => window.close(), 150);
    };

    if (providerError) {
      setMessage(providerError);
      report({ error: providerError });
      return;
    }

    if (!code || !state) {
      const error = 'The provider did not return an authorization code.';
      setMessage(error);
      report({ error });
      return;
    }

    api.completeOAuth(code, state)
      .then(connection => {
        setMessage('Connected. You can close this window.');
        report({ connection });
      })
      .catch((error: unknown) => {
        const text = error instanceof Error ? error.message : 'Could not complete sign-in.';
        setMessage(text);
        report({ error: text });
      });
  }, []);

  return (
    <div style={{
      minHeight: '100vh', display: 'grid', placeItems: 'center',
      background: c.canvas, padding: 24, textAlign: 'center',
    }}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 10, maxWidth: 360 }}>
        <div style={{
          width: 30, height: 30, borderRadius: 8, background: c.ink, color: '#fff',
          display: 'grid', placeItems: 'center', fontWeight: 700, fontSize: 15, margin: '0 auto',
        }}>D</div>
        <div style={{ fontSize: 15, color: c.muted, lineHeight: 1.5 }}>{message}</div>
      </div>
    </div>
  );
}
