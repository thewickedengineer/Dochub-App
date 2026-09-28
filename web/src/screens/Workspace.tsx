import { useEffect, useMemo, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { api } from '../lib/api';
import { useApp } from '../lib/AppContext';
import { formatBytes, formatRelative, useQuery } from '../lib/hooks';
import { c, srcOf, stOf } from '../theme';
import { Button, Empty, Field, Input, Modal, Select, SourceMark, StatusPill } from '../components/ui';
import AddSourceModal from '../components/AddSourceModal';

type CreateKind = 'team' | 'group' | 'artifact';

const CATEGORIES = ['Source code', 'Technical documentation', 'Business workflow', 'Policy documents', 'Operating procedures'];
const PRIMARY_SOURCES = [
  { value: 'GitHub', label: 'GitHub repository' },
  { value: 'SharePoint', label: 'SharePoint' },
  { value: 'GoogleDrive', label: 'Google Drive' },
  { value: 'Local', label: 'Local files / folder' },
  { value: 'AzureDevOps', label: 'Azure DevOps' },
];

export default function Workspace() {
  const { showToast, invalidate } = useApp();
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const [selectedId, setSelectedId] = useState<string>(params.get('artifact') ?? '');
  const [create, setCreate] = useState<{ kind: CreateKind; parentId?: string } | null>(null);
  const [addOpen, setAddOpen] = useState(false);

  const teams = useQuery(() => api.teams(), []);
  const connections = useQuery(() => api.connections(), []);

  const allArtifacts = useMemo(
    () => (teams.data ?? []).flatMap(t => t.groups.flatMap(g => g.artifacts)),
    [teams.data],
  );

  useEffect(() => {
    if (allArtifacts.length > 0 && !allArtifacts.some(a => a.id === selectedId)) {
      setSelectedId(allArtifacts[0].id);
    }
  }, [allArtifacts, selectedId]);

  const detail = useQuery(
    () => selectedId ? api.artifact(selectedId) : Promise.resolve(null),
    [selectedId],
  );

  const artifact = detail.data?.artifact;
  const documents = detail.data?.documents ?? [];

  const select = (id: string) => {
    setSelectedId(id);
    setParams({ artifact: id }, { replace: true });
  };

  const counts = artifact ? [
    { label: 'Indexed', value: artifact.indexedDocuments, dot: stOf('Indexed').dot },
    { label: 'Pending', value: artifact.pendingDocuments, dot: stOf('Pending').dot },
    { label: 'Processing', value: artifact.processingDocuments, dot: stOf('Processing').dot },
    { label: 'Failed', value: artifact.failedDocuments, dot: stOf('Failed').dot },
  ] : [];

  return (
    <>
      <div style={{
        display: 'grid', gridTemplateColumns: '240px minmax(0,1fr)', background: c.surface,
        border: `1px solid ${c.border}`, borderRadius: 14, overflow: 'hidden', minHeight: 620,
      }}>
        <div style={{ borderRight: `1px solid ${c.rule}`, background: c.sidebar, display: 'flex', flexDirection: 'column' }}>
          <div style={{ display: 'flex', alignItems: 'center', padding: '14px 14px 10px' }}>
            <span style={{ flex: 1, fontWeight: 600, fontSize: 14 }}>Teams</span>
            <Button
              style={{ height: 28, padding: '0 10px', fontSize: 12 }}
              onClick={() => setCreate({ kind: 'team' })}
            >+ Team</Button>
          </div>

          <div style={{ padding: '0 8px 14px', display: 'flex', flexDirection: 'column', gap: 2, overflow: 'auto' }}>
            {(teams.data ?? []).length === 0 && !teams.loading && (
              <div style={{ padding: '20px 10px', fontSize: 13, color: c.dim, lineHeight: 1.5 }}>
                No teams yet. Create one to get started — for insurance, that might be Claims or Policy.
              </div>
            )}
            {(teams.data ?? []).map(team => (
              <div key={team.id} style={{ display: 'flex', flexDirection: 'column' }}>
                <div style={{ display: 'flex', alignItems: 'center', gap: 8, height: 32, padding: '0 8px', fontSize: 14, fontWeight: 600 }}>
                  <span style={{
                    width: 20, height: 20, borderRadius: 5, background: '#E6E8EE',
                    display: 'grid', placeItems: 'center', fontSize: 10, flex: 'none',
                  }}>{team.name[0]}</span>
                  <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                    {team.name}
                  </span>
                  <button
                    onClick={() => setCreate({ kind: 'group', parentId: team.id })}
                    title="New group"
                    style={{ border: 0, background: 'transparent', color: c.dim, cursor: 'pointer', fontSize: 12, fontFamily: 'inherit' }}
                  >+ Group</button>
                </div>

                {team.groups.map(group => (
                  <div key={group.id} style={{ display: 'flex', flexDirection: 'column', paddingLeft: 18 }}>
                    <div style={{ display: 'flex', alignItems: 'center', gap: 8, height: 28, padding: '0 8px', fontSize: 13, color: c.soft, fontWeight: 500 }}>
                      <span style={{ color: '#B0B3BA' }}>▾</span>
                      <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                        {group.name}
                      </span>
                      <button
                        onClick={() => setCreate({ kind: 'artifact', parentId: group.id })}
                        title="New artifact"
                        style={{ border: 0, background: 'transparent', color: c.dim, cursor: 'pointer', fontSize: 12, fontFamily: 'inherit' }}
                      >+ Artifact</button>
                    </div>

                    {group.artifacts.map(a => {
                      const on = a.id === selectedId;
                      return (
                        <button
                          key={a.id}
                          onClick={() => select(a.id)}
                          style={{
                            marginLeft: 14, display: 'flex', alignItems: 'center', gap: 8, height: 30,
                            padding: '0 8px', border: 0, borderRadius: 7, fontSize: 13, cursor: 'pointer',
                            textAlign: 'left', fontFamily: 'inherit',
                            background: on ? c.active : 'transparent',
                            color: on ? c.ink : c.soft, fontWeight: on ? 600 : 400,
                          }}
                        >
                          <SourceMark source={a.primarySource} size={18} />
                          <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                            {a.name}
                          </span>
                          {a.pendingDocuments > 0 && (
                            <span style={{ width: 7, height: 7, borderRadius: '50%', background: '#D08A1E', flex: 'none' }} />
                          )}
                        </button>
                      );
                    })}
                  </div>
                ))}
              </div>
            ))}
          </div>
        </div>

        <div style={{ display: 'flex', flexDirection: 'column', minWidth: 0, overflowX: 'auto' }}>
          {!artifact && <Empty>Select an artifact, or create one to begin.</Empty>}

          {artifact && (
            <div style={{ minWidth: 620, display: 'flex', flexDirection: 'column' }}>
              <div style={{ padding: '20px 24px', borderBottom: `1px solid ${c.rule}`, display: 'flex', flexDirection: 'column', gap: 14 }}>
                <div style={{ fontSize: 13, color: c.dim }}>{artifact.teamName} › {artifact.groupName}</div>

                <div style={{ display: 'flex', alignItems: 'flex-start', gap: 16, flexWrap: 'wrap' }}>
                  <div style={{ flex: 1, minWidth: 240, display: 'flex', flexDirection: 'column', gap: 8 }}>
                    <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                      <SourceMark source={artifact.primarySource} size={32} />
                      <span style={{ fontSize: 22, fontWeight: 600, letterSpacing: '-0.02em' }}>{artifact.name}</span>
                    </div>
                    <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', fontSize: 12 }}>
                      {[
                        `Category · ${artifact.category}`,
                        `Source · ${srcOf(artifact.primarySource).label}`,
                        `${artifact.totalDocuments} documents`,
                      ].map(chip => (
                        <span key={chip} style={{
                          border: `1px solid ${c.border}`, borderRadius: 99, padding: '3px 10px',
                          color: c.soft, whiteSpace: 'nowrap',
                        }}>{chip}</span>
                      ))}
                    </div>
                  </div>

                  <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                    <Button onClick={() => navigate(`/upload?artifact=${artifact.id}`)}>
                      Add documents
                    </Button>
                  </div>
                </div>

                <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4,minmax(0,1fr))', gap: 10 }}>
                  {counts.map(count => (
                    <div key={count.label} style={{
                      border: `1px solid ${c.rule}`, borderRadius: 10, padding: '10px 12px',
                      display: 'flex', flexDirection: 'column', gap: 2,
                    }}>
                      <span style={{ fontSize: 12, color: c.muted, display: 'flex', alignItems: 'center', gap: 6 }}>
                        <span style={{ width: 7, height: 7, borderRadius: '50%', background: count.dot }} />
                        {count.label}
                      </span>
                      <span style={{ fontSize: 20, fontWeight: 600, fontVariantNumeric: 'tabular-nums' }}>{count.value}</span>
                    </div>
                  ))}
                </div>
              </div>

              <div style={{
                display: 'grid', gridTemplateColumns: 'minmax(160px,2.4fr) minmax(0,1fr) 70px 104px 84px',
                gap: 12, padding: '10px 24px', fontSize: 12, color: c.dim, fontWeight: 500,
                borderBottom: `1px solid ${c.rule}`, background: c.sidebar,
              }}>
                <span>Document</span><span>Location</span>
                <span style={{ textAlign: 'right' }}>Size</span>
                <span>Status</span><span>Updated</span>
              </div>

              {documents.length === 0 && <Empty>No documents yet. Use “Add documents” to import a source.</Empty>}

              {documents.map(d => (
                <div key={d.id} style={{
                  display: 'grid', gridTemplateColumns: 'minmax(160px,2.4fr) minmax(0,1fr) 70px 104px 84px',
                  gap: 12, padding: '11px 24px', fontSize: 14, alignItems: 'center',
                  borderBottom: `1px solid ${c.ruleSoft}`,
                }}>
                  <span style={{ minWidth: 0, display: 'flex', alignItems: 'center', gap: 8 }}>
                    <SourceMark source={d.sourceType} size={20} />
                    <span
                      title={d.error ?? d.name}
                      style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontWeight: 500 }}
                    >{d.name}</span>
                    {d.revision > 1 && (
                      <span
                        title={`Refreshed by a scheduled update${d.lastSyncedAt ? ` ${formatRelative(d.lastSyncedAt)}` : ''}`}
                        style={{
                          flex: 'none', fontSize: 11, fontWeight: 500, borderRadius: 99,
                          padding: '1px 7px', background: c.track, color: c.muted,
                          fontFamily: "'Geist Mono', monospace",
                        }}
                      >v{d.revision}</span>
                    )}
                  </span>
                  <span
                    title={d.blobPath ?? d.sourceLocation}
                    style={{
                      fontSize: 12, color: c.dim, fontFamily: "'Geist Mono', monospace",
                      overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                    }}
                  >{d.blobPath ?? d.sourceLocation}</span>
                  <span style={{ textAlign: 'right', fontSize: 13, color: c.muted, fontVariantNumeric: 'tabular-nums' }}>
                    {formatBytes(d.sizeBytes)}
                  </span>
                  <StatusPill status={d.status} />
                  <span style={{ fontSize: 13, color: c.muted }}>{formatRelative(d.updatedAt)}</span>
                </div>
              ))}
            </div>
          )}
        </div>
      </div>

      {create && (
        <CreateModal
          kind={create.kind}
          parentId={create.parentId}
          onClose={() => setCreate(null)}
          onDone={(label, newArtifactId) => {
            setCreate(null);
            showToast({ icon: '✓', dot: '#2E9A64', title: `${capitalize(create.kind)} created`, body: label });
            if (newArtifactId) select(newArtifactId);
            invalidate();
          }}
        />
      )}

      {addOpen && artifact && (
        <AddSourceModal
          artifact={artifact}
          initialSource={artifact.primarySource}
          connections={connections.data ?? []}
          onClose={() => setAddOpen(false)}
          onAdd={() => {
            setAddOpen(false);
            // Sources are submitted from the Upload screen, which owns the
            // staged list and the Process button.
            showToast({
              icon: 'i', dot: c.accent, title: 'Add sources from Upload',
              body: 'Choose this artifact on the Upload screen, add the source there, then press Process.',
            });
          }}
        />
      )}
    </>
  );
}

function CreateModal({ kind, parentId, onClose, onDone }: {
  kind: CreateKind; parentId?: string;
  onClose: () => void; onDone: (label: string, newArtifactId?: string) => void;
}) {
  const [name, setName] = useState('');
  const [category, setCategory] = useState(CATEGORIES[1]);
  const [source, setSource] = useState('GitHub');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const copy = {
    team: { title: 'Create team', sub: 'Teams organize work by business function, e.g. Claims or Policy.', ph: 'e.g. Billing' },
    group: { title: 'Create group', sub: 'Groups sit inside a team and hold artifacts.', ph: 'e.g. Claims IT' },
    artifact: { title: 'Create artifact', sub: 'An artifact holds one category of documents from a source.', ph: 'e.g. Claims IT Technical' },
  }[kind];

  const submit = async () => {
    if (!name.trim()) return;
    setBusy(true);
    setError(null);
    try {
      if (kind === 'team') await api.createTeam(name.trim());
      else if (kind === 'group') await api.createGroup(parentId!, name.trim());
      else {
        const created = await api.createArtifact(parentId!, name.trim(), category, source);
        onDone(name.trim(), created.id);
        return;
      }
      onDone(name.trim());
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not create it.');
      setBusy(false);
    }
  };

  return (
    <Modal
      title={copy.title}
      subtitle={copy.sub}
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
          placeholder={copy.ph}
          onChange={e => setName(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter') void submit(); }}
        />
      </Field>
      {kind === 'artifact' && (
        <>
          <Field label="Category">
            <Select value={category} onChange={e => setCategory(e.target.value)}>
              {CATEGORIES.map(x => <option key={x}>{x}</option>)}
            </Select>
          </Field>
          <Field label="Primary source">
            <Select value={source} onChange={e => setSource(e.target.value)}>
              {PRIMARY_SOURCES.map(x => <option key={x.value} value={x.value}>{x.label}</option>)}
            </Select>
          </Field>
        </>
      )}
    </Modal>
  );
}

const capitalize = (value: string) => value[0].toUpperCase() + value.slice(1);
