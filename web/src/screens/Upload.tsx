import { useEffect, useMemo, useRef, useState } from 'react';
import { api } from '../lib/api';
import { useApp } from '../lib/AppContext';
import { formatBytes, useQuery } from '../lib/hooks';
import { c, srcOf } from '../theme';
import type { PendingSource } from '../lib/types';
import { Button, Empty, PageHeading, Select, SourceMark } from '../components/ui';
import AddSourceModal from '../components/AddSourceModal';
import ProcessingList from '../components/ProcessingList';
import ScheduledUpdates from '../components/ScheduledUpdates';

/** The connector tiles under "or import from a source". */
const TILES = [
  { source: 'SharePoint', label: 'SharePoint / OneDrive' },
  { source: 'GoogleDrive', label: 'Google Drive' },
  { source: 'GitHub', label: 'GitHub' },
  { source: 'AzureDevOps', label: 'Azure DevOps' },
  { source: 'Confluence', label: 'Confluence' },
  { source: 'Jira', label: 'Jira' },
];

export default function Upload() {
  const { showToast, invalidate } = useApp();
  const [selectedId, setSelectedId] = useState('');
  const [addSource, setAddSource] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // Sources live here, in the browser, until Process is pressed. Local files are
  // held as File objects and only leave the machine at that point.
  const [pending, setPending] = useState<PendingSource[]>([]);
  const [localFiles, setLocalFiles] = useState<Record<string, File[]>>({});

  const fileInput = useRef<HTMLInputElement>(null);
  const folderInput = useRef<HTMLInputElement>(null);

  const artifacts = useQuery(() => api.artifacts(), []);
  const connections = useQuery(() => api.connections(), []);

  const list = artifacts.data ?? [];
  const selected = list.find(a => a.id === selectedId) ?? list[0];

  useEffect(() => {
    if (!selectedId && list.length > 0) setSelectedId(list[0].id);
  }, [list, selectedId]);

  const connectedSources = useMemo(
    () => new Set((connections.data ?? []).filter(x => x.status === 'Connected').map(x => x.sourceType)),
    [connections.data],
  );

  const totalFiles = pending.reduce((n, s) => n + (s.documents?.length ?? 0), 0);

  const addPending = (source: PendingSource, files?: File[]) => {
    setPending(current => [...current, source]);
    if (files) setLocalFiles(current => ({ ...current, [source.key]: files }));
  };

  const removePending = (key: string) => {
    setPending(current => current.filter(s => s.key !== key));
    setLocalFiles(current => {
      const next = { ...current };
      delete next[key];
      return next;
    });
  };

  const stageLocalFiles = (files: FileList | null) => {
    if (!files?.length) return;
    const chosen = Array.from(files);
    const key = crypto.randomUUID();
    const relative = (f: File) =>
      (f as File & { webkitRelativePath?: string }).webkitRelativePath || f.name;

    addPending({
      key,
      sourceType: 'Local',
      sourceReference: chosen.length === 1 ? chosen[0].name : `${chosen.length} local files`,
      documents: chosen.map(f => ({
        name: f.name,
        relativePath: relative(f),
        sizeBytes: f.size,
        contentType: f.type || undefined,
      })),
      summary: `${chosen.length} file${chosen.length === 1 ? '' : 's'} · ${formatBytes(chosen.reduce((n, f) => n + f.size, 0))}`,
    }, chosen);

    if (fileInput.current) fileInput.current.value = '';
    if (folderInput.current) folderInput.current.value = '';
  };

  const process = async () => {
    if (!selected || pending.length === 0) return;
    setBusy(true);
    try {
      // Local bytes go up first, immediately before the one call that matters.
      const prepared: PendingSource[] = [];
      for (const source of pending) {
        if (source.sourceType !== 'Local') { prepared.push(source); continue; }

        const files = localFiles[source.key] ?? [];
        const staged = await api.stage(files);
        prepared.push({
          ...source,
          options: { stagingIds: staged.map(f => f.stagingId) },
          documents: staged.map(f => ({
            name: f.name,
            relativePath: f.relativePath ?? f.name,
            sizeBytes: f.sizeBytes,
            contentType: f.contentType,
          })),
        });
      }

      const ack = await api.process(selected.id, prepared);
      showToast({ icon: '✓', dot: '#2E9A64', title: 'Request acknowledged', body: ack.message });
      setPending([]);
      setLocalFiles({});
      invalidate();
    } catch (e) {
      showToast({
        icon: '!', dot: '#C2412D', title: 'Could not start processing',
        body: e instanceof Error ? e.message : 'Unknown error.',
      });
    } finally { setBusy(false); }
  };

  if (!artifacts.loading && list.length === 0) {
    return (
      <>
        <PageHeading title="Upload documents" subtitle="Create a team, a group and an artifact before uploading anything." />
        <Empty>
          No artifacts yet. Head to <a href="/workspace" style={{ color: c.accent }}>Workspace</a> to create your first team.
        </Empty>
      </>
    );
  }

  return (
    <>
      <PageHeading
        title="Upload documents"
        subtitle="Pick a destination artifact and add the sources you want. Nothing is sent until you press Process."
      />

      <div style={{
        background: c.surface, border: `1px solid ${c.border}`, borderRadius: 16,
        boxShadow: '0 1px 2px rgba(22,24,29,.04), 0 10px 28px rgba(22,24,29,.05)',
        padding: 20, display: 'flex', flexDirection: 'column', gap: 18,
      }}>
        <div style={{ display: 'flex', alignItems: 'flex-end', gap: 12, flexWrap: 'wrap' }}>
          <label style={{ display: 'flex', flexDirection: 'column', gap: 6, fontSize: 13, fontWeight: 500, flex: 1, minWidth: 260, maxWidth: 480 }}>
            Upload to
            <Select value={selected?.id ?? ''} onChange={e => setSelectedId(e.target.value)}>
              {list.map(a => (
                <option key={a.id} value={a.id}>{a.teamName} › {a.groupName} › {a.name}</option>
              ))}
            </Select>
          </label>
          <a href="/workspace" style={{ height: 40, display: 'flex', alignItems: 'center', fontSize: 13, color: c.accent, fontWeight: 500, textDecoration: 'none' }}>
            + New artifact
          </a>
          <span style={{ flex: 1 }} />
          <Button
            variant={pending.length > 0 ? 'accent' : 'secondary'}
            disabled={busy || pending.length === 0}
            onClick={() => void process()}
            style={{ height: 40 }}
          >
            {busy ? 'Submitting…'
              : pending.length === 0 ? 'Nothing to process'
                : `Process ${pending.length} source${pending.length > 1 ? 's' : ''}`}
          </Button>
        </div>

        <div
          onClick={() => fileInput.current?.click()}
          onDragOver={e => e.preventDefault()}
          onDrop={e => { e.preventDefault(); stageLocalFiles(e.dataTransfer.files); }}
          style={{
            border: '1.5px dashed #CFCFC8', borderRadius: 14, padding: '32px 20px',
            display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 10,
            textAlign: 'center', background: c.sidebar, cursor: 'pointer',
          }}
        >
          <span style={{
            width: 48, height: 48, borderRadius: '50%', background: c.surface,
            border: `1px solid ${c.border}`, display: 'grid', placeItems: 'center',
            fontSize: 20, color: c.accent,
          }}>⇪</span>
          <div style={{ fontSize: 16, fontWeight: 600 }}>Drag files or folders here</div>
          <div style={{ fontSize: 13, color: c.muted }}>
            PDF, DOCX, XLSX, PPTX, MD, TXT, HTML · up to 200 MB per file
          </div>
          <div style={{ display: 'flex', gap: 8, marginTop: 4 }}>
            <Button style={{ height: 34, padding: '0 14px', fontSize: 13 }} onClick={() => fileInput.current?.click()}>Browse files</Button>
            <Button style={{ height: 34, padding: '0 14px', fontSize: 13 }} onClick={() => folderInput.current?.click()}>Browse folder</Button>
          </div>
          <input ref={fileInput} type="file" multiple hidden onChange={e => stageLocalFiles(e.target.files)} />
          <input
            ref={folderInput}
            type="file"
            hidden
            onChange={e => stageLocalFiles(e.target.files)}
            {...({ webkitdirectory: '', directory: '' } as Record<string, string>)}
          />
        </div>

        <div style={{
          display: 'flex', alignItems: 'center', gap: 12, fontSize: 12, color: c.dim,
          fontWeight: 500, letterSpacing: '.04em', textTransform: 'uppercase',
        }}>
          <span style={{ flex: 1, height: 1, background: c.rule }} />
          or import from a source
          <span style={{ flex: 1, height: 1, background: c.rule }} />
        </div>

        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill,minmax(180px,1fr))', gap: 10 }}>
          {TILES.map(tile => {
            const connected = connectedSources.has(tile.source);
            const s = srcOf(tile.source);
            return (
              <button
                key={tile.source}
                onClick={() => setAddSource(tile.source)}
                style={{
                  border: `1px solid ${c.border}`, background: c.surface, borderRadius: 10,
                  padding: '10px 12px', display: 'flex', alignItems: 'center', gap: 10,
                  cursor: 'pointer', textAlign: 'left', minWidth: 0, fontFamily: 'inherit',
                }}
              >
                <span style={{
                  width: 32, height: 32, borderRadius: 8, display: 'grid', placeItems: 'center',
                  flex: 'none', background: s.bg, color: s.fg, fontSize: 10, fontWeight: 700,
                }}>{s.mark}</span>
                <span style={{ display: 'flex', flexDirection: 'column', gap: 1, minWidth: 0 }}>
                  <span style={{ fontSize: 13, fontWeight: 600, color: c.ink, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
                    {tile.label}
                  </span>
                  <span style={{
                    fontSize: 11, fontWeight: 500, whiteSpace: 'nowrap', display: 'flex',
                    alignItems: 'center', gap: 5, color: connected ? '#1F7A4F' : '#94600F',
                  }}>
                    <span style={{ flex: 'none', width: 6, height: 6, borderRadius: '50%', background: connected ? '#1F7A4F' : '#94600F' }} />
                    {connected ? 'Connected · Import' : 'Not connected'}
                  </span>
                </span>
              </button>
            );
          })}
        </div>

        {pending.length > 0 && (
          <div style={{ border: `1px solid ${c.border}`, borderRadius: 12, overflow: 'hidden' }}>
            <div style={{
              display: 'flex', alignItems: 'center', gap: 10, padding: '10px 14px',
              background: c.sidebar, borderBottom: `1px solid ${c.rule}`,
            }}>
              <span style={{ flex: 1, fontSize: 13, fontWeight: 600 }}>
                Ready to process · {pending.length} source{pending.length > 1 ? 's' : ''}
                {totalFiles > 0 && `, ${totalFiles} file${totalFiles > 1 ? 's' : ''}`}
              </span>
              <span style={{ fontSize: 12, color: c.dim }}>Not sent yet</span>
              <button
                onClick={() => { setPending([]); setLocalFiles({}); }}
                style={{ border: 0, background: 'transparent', fontSize: 12, color: c.muted, cursor: 'pointer', fontWeight: 500 }}
              >Clear all</button>
            </div>

            {pending.map(source => (
              <div key={source.key} style={{
                display: 'flex', alignItems: 'center', gap: 10, padding: '12px 14px',
                borderBottom: `1px solid ${c.ruleSoft}`, minWidth: 0,
              }}>
                <SourceMark source={source.sourceType} size={28} />
                <div style={{ flex: 1, minWidth: 0 }}>
                  <div style={{ fontSize: 14, fontWeight: 500, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                    {source.sourceReference}
                  </div>
                  <div style={{ fontSize: 12, color: c.dim }}>
                    {source.summary}
                    {source.schedule && ` · keeps in sync ${source.schedule.frequency.toLowerCase()}`}
                  </div>
                </div>
                <button
                  onClick={() => removePending(source.key)}
                  title="Remove"
                  style={{
                    width: 26, height: 26, border: 0, borderRadius: 6, background: 'transparent',
                    color: c.dim, cursor: 'pointer', fontSize: 16, flex: 'none',
                  }}
                >×</button>
              </div>
            ))}
          </div>
        )}
      </div>

      <ScheduledUpdates />
      <ProcessingList />

      {addSource && selected && (
        <AddSourceModal
          artifact={selected}
          initialSource={addSource}
          connections={connections.data ?? []}
          onClose={() => setAddSource(null)}
          onAdd={source => { addPending(source); setAddSource(null); }}
        />
      )}
    </>
  );
}
