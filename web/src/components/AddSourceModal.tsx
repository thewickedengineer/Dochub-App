import { useState } from 'react';
import { api } from '../lib/api';
import { connectWithProvider } from '../lib/oauth';
import { useApp } from '../lib/AppContext';
import { useQuery } from '../lib/hooks';
import { c, srcOf } from '../theme';
import type {
  ArtifactSummaryDto, BrowseSelection, PendingSource, ResolvedLinkDto, SourceConnectionDto,
} from '../lib/types';
import { Button, Field, Input, Modal } from './ui';
import RemoveConnection from './RemoveConnection';
import FileBrowser from './FileBrowser';
import SyncSchedulePicker, {
  emptyDraft, toRequest, type ScheduleDraft, type UploadMode,
} from './SyncSchedulePicker';

/** Only these locations can be re-listed later, so only they can be kept in sync. */
const SYNCABLE = new Set(['SharePoint', 'GoogleDrive', 'GitHub']);

/** Syncable sources that need a stored token to be re-read unattended. Public GitHub repos don't. */
const SYNC_NEEDS_CONNECTION = new Set(['SharePoint', 'GoogleDrive']);

const SOURCES = ['GitHub', 'SharePoint', 'GoogleDrive', 'Local', 'AzureDevOps', 'Confluence', 'Jira'];

const AUTH_COPY: Record<string, { copy: string; button: string }> = {
  SharePoint: {
    copy: "Sign in with a Microsoft work, school or personal account to read SharePoint sites and OneDrive folders.",
    button: 'Sign in with Microsoft',
  },
  GoogleDrive: {
    copy: "Google Drive requires a Google access token. You'll be asked to grant read-only access to the folders you choose.",
    button: 'Sign in with Google',
  },
  AzureDevOps: {
    copy: 'Azure DevOps uses your Microsoft Entra ID token. Choose the organization, project and wiki to import.',
    button: 'Sign in with Microsoft',
  },
  Confluence: { copy: 'Connect your Atlassian account to import pages from Confluence spaces.', button: 'Connect Atlassian' },
  Jira: { copy: 'Connect your Atlassian account to import issues and epics from Jira projects.', button: 'Connect Atlassian' },
};

/** Sources that only need a token pasted in; GitHub and Local work without one. */
const NEEDS_TOKEN = new Set(['SharePoint', 'GoogleDrive', 'AzureDevOps', 'Confluence', 'Jira']);

/**
 * Collects one source's details. Nothing is sent here — the source is handed
 * back to the Upload screen, which holds it until Process is pressed.
 */
export default function AddSourceModal({
  artifact, initialSource, connections, onClose, onAdd,
}: {
  artifact: ArtifactSummaryDto;
  initialSource: string;
  connections: SourceConnectionDto[];
  onClose: () => void;
  onAdd: (source: PendingSource) => void;
}) {
  const { showToast, invalidate } = useApp();
  const [source, setSource] = useState(initialSource);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [token, setToken] = useState('');

  // GitHub
  const [repository, setRepository] = useState('');
  const [branch, setBranch] = useState('main');
  const [path, setPath] = useState('');
  const [fileTypes, setFileTypes] = useState('');
  const [githubToken, setGithubToken] = useState('');
  // Azure DevOps
  const [azOrg, setAzOrg] = useState('');
  const [azProject, setAzProject] = useState('');
  const [azWiki, setAzWiki] = useState('');
  const [link, setLink] = useState('');
  const [resolved, setResolved] = useState<ResolvedLinkDto | null>(null);
  const [picked, setPicked] = useState<BrowseSelection[]>([]);
  const [browsing, setBrowsing] = useState(false);
  const [resolving, setResolving] = useState(false);
  const [mode, setMode] = useState<UploadMode>('once');
  const [schedule, setSchedule] = useState<ScheduleDraft>(emptyDraft);

  const connection = connections.find(x => x.sourceType === source && x.status === 'Connected');
  const options = useQuery(() => api.connectionOptions(), []);
  const gitHubSignIn = options.data?.some(x => x.sourceType === 'GitHub' && x.signInConfigured) ?? false;
  const [pasteToken, setPasteToken] = useState(false);
  const [removing, setRemoving] = useState<SourceConnectionDto | null>(null);
  const needsAuth = NEEDS_TOKEN.has(source) && !connection;

  // Recurring needs somewhere to go back to and a token to go back with.
  const syncBlockedBecause = !SYNCABLE.has(source)
    ? `${srcOf(source).label} can't be kept in sync — only SharePoint / OneDrive, Google Drive and GitHub locations can be re-read on a schedule.`
    : !connection && SYNC_NEEDS_CONNECTION.has(source)
      ? `Connect ${srcOf(source).label} first: recurring updates run unattended and need a stored token.`
      : undefined;
  const wantsSchedule = mode === 'recurring' && !syncBlockedBecause;

  /** Sources that sign in through Google or Microsoft and are pointed at by a link. */
  const usesExternalSignIn = source === 'GoogleDrive' || source === 'SharePoint';

  const connect = async () => {
    setBusy(true);
    setError(null);
    try {
      if (usesExternalSignIn) {
        const created = await connectWithProvider(source);
        showToast({
          icon: '✓', dot: '#2E9A64',
          title: `${srcOf(source).label} connected`,
          body: created.account
            ? `Signed in as ${created.account}. Dochub can now read the documents you choose.`
            : 'Dochub can now read the documents you choose.',
        });
        invalidate();
        return;
      }

      if (!token.trim()) { setError('Paste the access token issued by the provider.'); return; }
      await api.connect(source, token.trim());
      showToast({
        icon: '✓', dot: '#2E9A64', title: `${srcOf(source).label} connected`,
        body: 'Access token stored securely for this workspace.',
      });
      setToken('');
      invalidate();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not connect.');
    } finally {
      setBusy(false);
    }
  };

  const connectGitHub = async () => {
    if (!githubToken.trim()) { setError('Paste a GitHub personal access token.'); return; }
    setBusy(true);
    setError(null);
    try {
      const created = await api.connect('GitHub', githubToken.trim());
      showToast({
        icon: '✓', dot: '#2E9A64', title: 'GitHub connected',
        body: created.account ? `Reading repositories as ${created.account}.` : 'Token stored for this workspace.',
      });
      setGithubToken('');
      invalidate();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not connect GitHub.');
    } finally {
      setBusy(false);
    }
  };

  const signInGitHub = async () => {
    setBusy(true);
    setError(null);
    try {
      const created = await connectWithProvider('GitHub');
      showToast({
        icon: '✓', dot: '#2E9A64', title: 'GitHub connected',
        body: created.account ? `Reading repositories as ${created.account}.` : 'Private repositories you can read are reachable.',
      });
      invalidate();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not connect GitHub.');
    } finally {
      setBusy(false);
    }
  };

  const resolveLink = async () => {
    if (!connection) { setError('Sign in first — resolving a link needs that account\'s access.'); return; }
    if (!link.trim()) { setError('Paste the link to the folder you want to import.'); return; }

    setResolving(true);
    setError(null);
    try {
      const result = await api.resolveLink(link.trim(), connection.id);
      setResolved(result);
      setPicked([]);
    } catch (e) {
      setResolved(null);
      setError(e instanceof Error ? e.message : 'Could not read that link.');
    } finally {
      setResolving(false);
    }
  };

  const add = () => {
    const { reference, options } = buildRequest();
    onAdd({
      key: crypto.randomUUID(),
      sourceType: source,
      sourceReference: reference,
      sourceConnectionId: connection?.id,
      options,
      schedule: wantsSchedule ? toRequest(schedule) : undefined,
      summary: describeSource(),
    });
  };

  const pickedSummary = () =>
    picked.length <= 3 ? picked.map(x => x.name).join(', ') : `${picked.slice(0, 2).map(x => x.name).join(', ')} and ${picked.length - 2} more`;

  const describeSource = () => {
    switch (source) {
      case 'GitHub': return `${repository.trim()} · ${branch || 'main'}${path ? ` · /${path}` : ''}${fileTypes ? ` · ${fileTypes}` : ''}`;
      case 'SharePoint':
      case 'GoogleDrive': return picked.length ? pickedSummary() : resolved?.displayName ?? 'Linked folder';
      case 'AzureDevOps': return `${azOrg} / ${azProject} / ${azWiki}`;
      default: return srcOf(source).label;
    }
  };

  const buildRequest = (): { reference: string; options: Record<string, unknown> } => {
    switch (source) {
      case 'GitHub':
        return {
          reference: `${repository.trim()}@${branch.trim() || 'main'}:/${path.trim()}`,
          options: {
            repository: repository.trim(), branch: branch.trim() || 'main', path: path.trim(),
            // The extractor takes a list; an empty one means every file.
            fileTypes: fileTypes.split(',').map(x => x.trim()).filter(Boolean),
          },
        };
      case 'SharePoint':
      case 'GoogleDrive':
        // Whatever the resolver read out of the pasted link.
        return {
          reference: picked.length ? `${srcOf(source).label} › ${pickedSummary()}` : resolved?.sourceReference ?? srcOf(source).label,
          // Picked in the browser: folders and files, each with what the importer needs.
          options: picked.length ? { items: picked } : resolved?.options ?? {},
        };
      case 'AzureDevOps':
        return {
          reference: `${azOrg}/${azProject}/_wiki/${azWiki}`,
          options: { organization: azOrg, project: azProject, wiki: azWiki },
        };
      default:
        return { reference: srcOf(source).label, options: {} };
    }
  };

  const canSubmit = !busy && !needsAuth && source !== 'Local'
    && (source !== 'GitHub' || repository.trim().length > 0)
    && (!usesExternalSignIn || resolved !== null || picked.length > 0)
    && (source !== 'AzureDevOps' || Boolean(azOrg && azProject && azWiki));

  return (
    <Modal
      title="Add a source"
      subtitle={`to ${artifact.name} — sent when you press Process`}
      width={640}
      onClose={onClose}
      footer={<>
        <span style={{ flex: 1, fontSize: 13, color: error ? '#B13A26' : c.muted }}>
          {error ?? <>Nothing is sent yet. The source joins the list below, and <b style={{ color: c.ink }}>Process</b> submits it.</>}
        </span>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="primary" disabled={!canSubmit} onClick={add}>Add source</Button>
      </>}
    >
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill,minmax(130px,1fr))', gap: 8 }}>
        {SOURCES.map(key => {
          const s = srcOf(key);
          const isConnected = connections.some(x => x.sourceType === key && x.status === 'Connected');
          return (
            <button
              key={key}
              onClick={() => { setSource(key); setError(null); }}
              style={{
                borderRadius: 10, padding: '12px 10px', cursor: 'pointer', display: 'flex',
                flexDirection: 'column', alignItems: 'flex-start', gap: 8, textAlign: 'left',
                background: c.surface, fontFamily: 'inherit',
                border: key === source ? `2px solid ${c.accent}` : `1px solid ${c.border}`,
              }}
            >
              <span style={{
                width: 26, height: 26, borderRadius: 6, display: 'grid', placeItems: 'center',
                fontSize: 10, fontWeight: 700, background: s.bg, color: s.fg,
              }}>{s.mark}</span>
              <span style={{ fontSize: 13, fontWeight: 500, color: c.ink }}>{s.label}</span>
              <span style={{ fontSize: 11, color: isConnected ? '#1F7A4F' : '#94600F' }}>
                {key === 'Local' ? 'Upload' : isConnected ? 'Connected' : 'Not connected'}
              </span>
            </button>
          );
        })}
      </div>

      <div style={{ display: 'flex', flexDirection: 'column', gap: 12, minHeight: 170, paddingTop: 6 }}>
        {needsAuth && (
          <div style={{
            border: `1px dashed ${c.borderStrong}`, borderRadius: 12, padding: 22,
            display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 10, textAlign: 'center',
          }}>
            <div style={{ fontWeight: 600, fontSize: 15 }}>Connect {srcOf(source).label}</div>
            <div style={{ fontSize: 13, color: c.muted, maxWidth: 420, lineHeight: 1.5 }}>
              {AUTH_COPY[source]?.copy}
            </div>

            {usesExternalSignIn ? (
              <>
                <Button variant="primary" disabled={busy} onClick={() => void connect()}>
                  {busy ? 'Waiting for sign-in…' : AUTH_COPY[source]?.button ?? 'Sign in'}
                </Button>
                <div style={{ fontSize: 12, color: c.dim, maxWidth: 420, lineHeight: 1.5 }}>
                  Opens {srcOf(source).label} in a new window. Dochub asks for read-only
                  access and never sees your password.
                </div>
              </>
            ) : (
              <>
                <Input
                  value={token}
                  placeholder="Paste the OAuth access token"
                  onChange={e => setToken(e.target.value)}
                  style={{ maxWidth: 420 }}
                />
                <Button variant="primary" disabled={busy} onClick={() => void connect()}>
                  {AUTH_COPY[source]?.button ?? 'Connect'}
                </Button>
              </>
            )}
          </div>
        )}

        {!needsAuth && usesExternalSignIn && (
          <>
            <div style={{
              display: 'flex', alignItems: 'center', gap: 8, fontSize: 12,
              color: '#1F7A4F', background: '#E7F4EC', borderRadius: 8, padding: '7px 10px',
            }}>
              <span>✓</span>
              <span style={{ flex: 1 }}>
                Signed in{connection?.account ? ` as ${connection.account}` : ''}
                {connection?.canRefresh ? ' · stays connected' : ''}
              </span>
              <button
                onClick={() => void connect()}
                disabled={busy}
                style={{ border: 0, background: 'transparent', color: '#1F7A4F', cursor: 'pointer', fontSize: 12, fontWeight: 500 }}
              >Switch account</button>
              <button
                onClick={() => connection && setRemoving(connection)}
                disabled={busy}
                style={{ border: 0, background: 'transparent', color: '#B13A26', cursor: 'pointer', fontSize: 12, fontWeight: 500 }}
              >Remove</button>
            </div>

            <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
              <Button variant="primary" onClick={() => setBrowsing(true)}>
                {source === 'GoogleDrive' ? 'Browse Google Drive' : 'Browse SharePoint / OneDrive'}
              </Button>
              <span style={{ fontSize: 12, color: c.dim }}>Pick folders and files, the way you would in {srcOf(source).label}.</span>
            </div>

            {picked.length > 0 && (
              <div style={{ border: `1px solid ${c.border}`, borderRadius: 10, padding: '8px 10px', background: c.accentTint, display: 'flex', flexDirection: 'column', gap: 4 }}>
                {picked.map(item => (
                  <div key={`${item.driveId ?? ''}/${item.id}`} style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: 13 }}>
                    <span>{item.folder ? '📁' : '📄'}</span>
                    <span style={{ flex: 1, color: c.ink, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{item.name}</span>
                    <button
                      onClick={() => setPicked(picked.filter(x => x !== item))}
                      style={{ border: 0, background: 'transparent', color: c.muted, cursor: 'pointer', fontSize: 13 }}
                      aria-label={`Remove ${item.name}`}
                    >×</button>
                  </div>
                ))}
              </div>
            )}

            <Field label={`Or paste a link to a ${source === 'GoogleDrive' ? 'Drive' : 'SharePoint or OneDrive'} folder`}>
              <div style={{ display: 'flex', gap: 8 }}>
                <Input
                  value={link}
                  placeholder={source === 'GoogleDrive'
                    ? 'https://drive.google.com/drive/folders/…'
                    : 'A SharePoint folder, or a OneDrive folder or share link'}
                  onChange={e => { setLink(e.target.value); setResolved(null); }}
                  onKeyDown={e => { if (e.key === 'Enter') void resolveLink(); }}
                />
                <Button disabled={resolving || !link.trim()} onClick={() => void resolveLink()}>
                  {resolving ? 'Checking…' : 'Check'}
                </Button>
              </div>
            </Field>

            {picked.length > 0 ? null : resolved ? (
              <div style={{
                display: 'flex', alignItems: 'center', gap: 10, fontSize: 13,
                border: `1px solid ${c.border}`, borderRadius: 10, padding: '10px 12px', background: c.accentTint,
              }}>
                <span style={{ fontSize: 15 }}>📁</span>
                <div style={{ flex: 1, minWidth: 0 }}>
                  <div style={{ fontWeight: 600, color: c.ink }}>{resolved.displayName}</div>
                  <div style={{ fontSize: 12, color: c.muted }}>
                    Found, and readable by the signed-in account.
                  </div>
                </div>
              </div>
            ) : (
              <div style={{ fontSize: 12, color: c.dim, lineHeight: 1.5 }}>
                Open the folder in {srcOf(source).label}, copy the address, and paste it
                here — Dochub works out the rest.
              </div>
            )}
          </>
        )}

        {!needsAuth && source === 'GitHub' && (
          <>
            {connection ? (
              <div style={{
                display: 'flex', alignItems: 'center', gap: 8, fontSize: 12,
                color: '#1F7A4F', background: '#E7F4EC', borderRadius: 8, padding: '7px 10px',
              }}>
                <span>✓</span>
                <span style={{ flex: 1 }}>Connected{connection.account ? ` as ${connection.account}` : ''} — private repositories it can read are reachable.</span>
                <button
                  onClick={() => setRemoving(connection)}
                  disabled={busy}
                  style={{ border: 0, background: 'transparent', color: '#B13A26', cursor: 'pointer', fontSize: 12, fontWeight: 500 }}
                >Remove</button>
              </div>
            ) : (
              <div style={{ border: `1px dashed ${c.borderStrong}`, borderRadius: 10, padding: '10px 12px', display: 'flex', flexDirection: 'column', gap: 8 }}>
                <div style={{ fontSize: 13, color: c.soft }}>
                  <b>Not connected.</b> Public repositories work as they are. For a private one, sign in to GitHub
                  in a separate window and approve Dochub's access.
                </div>
                <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
                  <Button variant="primary" disabled={busy || !gitHubSignIn} onClick={() => void signInGitHub()}>
                    {busy && !pasteToken ? 'Waiting for sign-in…' : 'Sign in with GitHub'}
                  </Button>
                  {!pasteToken && (
                    <button
                      onClick={() => setPasteToken(true)}
                      style={{ border: 0, background: 'transparent', color: c.dim, cursor: 'pointer', fontSize: 12 }}
                    >Advanced: use a token</button>
                  )}
                </div>
                {!gitHubSignIn && options.data && (
                  <div style={{ fontSize: 12, color: '#94600F', lineHeight: 1.5 }}>
                    GitHub sign-in isn't set up on this Dochub yet: an administrator registers a GitHub OAuth App
                    and adds its client id and secret under <code>OAuth:GitHub</code>.
                  </div>
                )}
                {pasteToken && (<>
                <div style={{ fontSize: 12, color: c.dim, lineHeight: 1.5 }}>
                  A GitHub{' '}
                  <a href="https://github.com/settings/personal-access-tokens/new" target="_blank" rel="noreferrer" style={{ color: c.accent }}>
                    fine-grained personal access token</a> with read-only <i>Contents</i> access to the repositories you need.
                </div>
                <div style={{ display: 'flex', gap: 8 }}>
                  <Input
                    value={githubToken}
                    type="password"
                    placeholder="github_pat_…"
                    onChange={e => setGithubToken(e.target.value)}
                    onKeyDown={e => { if (e.key === 'Enter') void connectGitHub(); }}
                  />
                  <Button disabled={busy || !githubToken.trim()} onClick={() => void connectGitHub()}>
                    {busy ? 'Checking…' : 'Connect'}
                  </Button>
                  <Button variant="ghost" onClick={() => { setPasteToken(false); setGithubToken(''); }}>Cancel</Button>
                </div>
                </>)}
              </div>
            )}
            <Field label="Repository">
              <Input
                value={repository}
                placeholder="owner/name, or https://github.com/owner/name"
                onChange={e => setRepository(e.target.value)}
                style={{ fontFamily: "'Geist Mono', monospace" }}
              />
            </Field>
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr 1fr', gap: 10 }}>
              <Field label="Branch"><Input value={branch} placeholder="main" onChange={e => setBranch(e.target.value)} /></Field>
              <Field label="Folder (optional)"><Input value={path} onChange={e => setPath(e.target.value)} placeholder="whole repository" /></Field>
              <Field label="File types (optional)"><Input value={fileTypes} onChange={e => setFileTypes(e.target.value)} placeholder="all, or e.g. md, py" /></Field>
            </div>
          </>
        )}



        {!needsAuth && source === 'AzureDevOps' && (
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr 1fr', gap: 10 }}>
            <Field label="Organization"><Input value={azOrg} onChange={e => setAzOrg(e.target.value)} /></Field>
            <Field label="Project"><Input value={azProject} onChange={e => setAzProject(e.target.value)} /></Field>
            <Field label="Wiki"><Input value={azWiki} onChange={e => setAzWiki(e.target.value)} /></Field>
          </div>
        )}

        {source === 'Local' && (
          <div style={{
            border: `1.5px dashed ${c.borderStrong}`, borderRadius: 12, padding: 30,
            display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 6,
            textAlign: 'center', background: c.sidebar,
          }}>
            <div style={{ fontWeight: 600, fontSize: 15 }}>Use the drop zone on the Upload page</div>
            <div style={{ fontSize: 13, color: c.muted }}>
              Local files are streamed straight from the browser, so they're added there rather than here.
            </div>
          </div>
        )}

        {!needsAuth && source !== 'Local' && (
          <div style={{ borderTop: `1px solid ${c.rule}`, paddingTop: 14, marginTop: 2 }}>
            <SyncSchedulePicker
              mode={mode}
              onModeChange={setMode}
              draft={schedule}
              onDraftChange={setSchedule}
              disabledReason={syncBlockedBecause}
            />
          </div>
        )}

        {!needsAuth && (source === 'Confluence' || source === 'Jira') && (
          <div style={{ fontSize: 13, color: c.muted, lineHeight: 1.5 }}>
            The {srcOf(source).label} connection is stored. An extractor for this source is not wired up yet —
            registering an upload will record the batch and report the unsupported source on the status row.
          </div>
        )}
      </div>

      {browsing && connection && (
        <FileBrowser
          connection={connection}
          onClose={() => setBrowsing(false)}
          onPick={items => {
            // Picking again adds to the list; the same item is never listed twice.
            const seen = new Set(picked.map(x => `${x.driveId ?? ''}/${x.id}`));
            setPicked([...picked, ...items.filter(x => !seen.has(`${x.driveId ?? ''}/${x.id}`))]);
            setResolved(null);
            setLink('');
            setBrowsing(false);
          }}
        />
      )}

      {removing && (
        <RemoveConnection
          connection={removing}
          onClose={() => setRemoving(null)}
          onRemoved={() => {
            showToast({
              icon: '✓', dot: '#2E9A64', title: `${removing.displayName} removed`,
              body: 'Connect again below whenever you need it.',
            });
            setRemoving(null);
            setResolved(null);
            setPicked([]);
            invalidate();
          }}
        />
      )}
    </Modal>
  );
}
