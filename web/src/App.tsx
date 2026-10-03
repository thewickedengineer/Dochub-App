import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import { AppProvider, useApp } from './lib/AppContext';
import Shell from './components/Shell';
import SignIn from './screens/SignIn';
import OAuthCallback from './screens/OAuthCallback';
import Upload from './screens/Upload';
import KnowledgeBase from './screens/KnowledgeBase';
import Workspace from './screens/Workspace';
import Members from './screens/Members';
import Connections from './screens/Connections';
import Ask from './screens/Ask';
import Chat from './screens/Chat';
import Platform from './screens/Platform';
import { c } from './theme';

export default function App() {
  return (
    <AppProvider>
      <BrowserRouter>
        <Routes>
          {/* One callback for both: signing in to Dochub (full page, id_token in
              the fragment) and connecting a source (popup, code in the query). */}
          <Route path="/oauth/callback" element={<OAuthCallback />} />
          {/* Kept so anything still pointed at the old path keeps working. */}
          <Route path="/auth/callback" element={<OAuthCallback />} />
          <Route path="/*" element={<Router />} />
        </Routes>
      </BrowserRouter>
    </AppProvider>
  );
}

function Router() {
  const { ready, user, organization, signOut } = useApp();

  if (!ready) return <Splash>Loading Dochub…</Splash>;
  if (!user) return <SignIn />;

  // A Creator needn't belong to any organization: they set them up for others.
  if (!organization && user.isCreator) {
    return (
      <div style={{ minHeight: '100vh', background: c.canvas }}>
        <header style={{
          height: 60, background: c.surface, borderBottom: `1px solid ${c.border}`,
          display: 'flex', alignItems: 'center', gap: 10, padding: '0 24px',
        }}>
          <div style={{
            width: 30, height: 30, borderRadius: 8, background: c.ink, color: '#fff',
            display: 'grid', placeItems: 'center', fontWeight: 700, fontSize: 15,
          }}>D</div>
          <span style={{ fontWeight: 700, fontSize: 18, letterSpacing: '-0.02em' }}>Dochub</span>
          <span style={{ fontSize: 12, color: c.muted, border: `1px solid ${c.border}`, borderRadius: 99, padding: '2px 8px' }}>Creator</span>
          <span style={{ flex: 1 }} />
          <span style={{ fontSize: 13, color: c.muted }}>{user.email}</span>
          <button onClick={signOut} style={{
            border: 0, background: 'transparent', color: c.accent, cursor: 'pointer', fontSize: 13, fontFamily: 'inherit',
          }}>Sign out</button>
        </header>
        <main style={{ padding: '28px 32px', display: 'flex', justifyContent: 'center' }}><Platform /></main>
      </div>
    );
  }

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
        <Route path="/connections" element={<Connections />} />
        <Route path="/ask" element={<Ask />} />
        <Route path="/chat" element={<Chat />} />
        <Route path="/chat/:id" element={<Chat />} />
        {user.isCreator && <Route path="/platform" element={<Platform />} />}
        <Route path="*" element={<Navigate to="/upload" replace />} />
      </Routes>
    </Shell>
  );
}

function Splash({ children }: { children: React.ReactNode }) {
  return (
    <div style={{
      minHeight: '100vh', display: 'grid', placeItems: 'center',
      background: c.canvas, color: c.muted, fontSize: 15, padding: 24,
    }}>{children}</div>
  );
}
