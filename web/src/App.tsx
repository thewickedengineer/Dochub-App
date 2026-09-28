import { useEffect } from 'react';
import { BrowserRouter, Navigate, Route, Routes, useNavigate } from 'react-router-dom';
import { AppProvider, useApp } from './lib/AppContext';
import Shell from './components/Shell';
import SignIn from './screens/SignIn';
import OAuthCallback from './screens/OAuthCallback';
import Upload from './screens/Upload';
import KnowledgeBase from './screens/KnowledgeBase';
import Workspace from './screens/Workspace';
import Members from './screens/Members';
import Ask from './screens/Ask';
import { c } from './theme';

export default function App() {
  return (
    <AppProvider>
      <BrowserRouter>
        <Routes>
          <Route path="/auth/callback" element={<MicrosoftCallback />} />
          {/* Runs inside the OAuth popup, outside the signed-in shell. */}
          <Route path="/oauth/callback" element={<OAuthCallback />} />
          <Route path="/*" element={<Router />} />
        </Routes>
      </BrowserRouter>
    </AppProvider>
  );
}

function Router() {
  const { ready, user, organization } = useApp();

  if (!ready) return <Splash>Loading Dochub…</Splash>;
  if (!user) return <SignIn />;

  // Signed in but not yet placed in an organization: an owner has to add them.
  if (!organization) {
    return (
      <Splash>
        <div style={{ maxWidth: 420, textAlign: 'center', display: 'flex', flexDirection: 'column', gap: 10 }}>
          <div style={{ fontSize: 20, fontWeight: 600 }}>You're signed in, {user.displayName}.</div>
          <div style={{ color: c.muted, fontSize: 15, lineHeight: 1.55 }}>
            You don't belong to an organization yet. An owner needs to add
            <b style={{ color: c.ink }}> {user.email}</b> before you can see any documents.
          </div>
        </div>
      </Splash>
    );
  }

  return (
    <Shell>
      <Routes>
        <Route path="/" element={<Navigate to="/upload" replace />} />
        <Route path="/upload" element={<Upload />} />
        <Route path="/knowledge-base" element={<KnowledgeBase />} />
        <Route path="/workspace" element={<Workspace />} />
        <Route path="/members" element={<Members />} />
        <Route path="/ask" element={<Ask />} />
        <Route path="*" element={<Navigate to="/upload" replace />} />
      </Routes>
    </Shell>
  );
}

/** Completes the Microsoft implicit flow: the ID token comes back in the fragment. */
function MicrosoftCallback() {
  const { signIn } = useApp();
  const navigate = useNavigate();

  useEffect(() => {
    const fragment = new URLSearchParams(window.location.hash.slice(1));
    const idToken = fragment.get('id_token');
    if (!idToken) { navigate('/', { replace: true }); return; }
    signIn('microsoft', idToken)
      .then(() => navigate('/upload', { replace: true }))
      .catch(() => navigate('/', { replace: true }));
  }, [signIn, navigate]);

  return <Splash>Completing sign-in…</Splash>;
}

function Splash({ children }: { children: React.ReactNode }) {
  return (
    <div style={{
      minHeight: '100vh', display: 'grid', placeItems: 'center',
      background: c.canvas, color: c.muted, fontSize: 15, padding: 24,
    }}>{children}</div>
  );
}
