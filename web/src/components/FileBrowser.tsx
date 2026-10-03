import { useCallback, useEffect, useRef, useState } from 'react';
import { api } from '../lib/api';
import { formatBytes, formatRelative } from '../lib/hooks';
import type { BrowseItem, BrowseSelection, SourceConnectionDto } from '../lib/types';
import { c } from '../theme';
import { Button, Input, Modal } from './ui';

interface Crumb { name: string; location: string; query?: string }

const ICON: Record<string, string> = {
  folder: '📁', file: '📄', site: '🏢', library: '🗄️',
  'place:onedrive': '☁️', 'place:mydrive': '☁️', 'place:shared': '👥', 'place:sites': '🏢', 'place:drives': '🗄️',
};

const keyOf = (s: BrowseSelection) => `${s.driveId ?? ''}/${s.id}`;

/**
 * Browses a connected SharePoint / OneDrive or Google Drive account the way their
 * own file pickers do: places on the left, folders on the right, tick what to import.
 * Folders and single files can be mixed, across locations.
 */
export default function FileBrowser({ connection, onClose, onPick }: {
  connection: SourceConnectionDto;
  onClose: () => void;
  onPick: (items: BrowseSelection[]) => void;
}) {
  const google = connection.sourceType === 'GoogleDrive';
  const [places, setPlaces] = useState<BrowseItem[]>([]);
  const [trail, setTrail] = useState<Crumb[]>([]);
  const [items, setItems] = useState<BrowseItem[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  // Only the latest request may fill the list: a slow folder must not overwrite the one opened after it.
  const request = useRef(0);
  const [error, setError] = useState<string | null>(null);
  const [search, setSearch] = useState('');
  const [picked, setPicked] = useState<Map<string, BrowseSelection>>(new Map());

  const here = trail[trail.length - 1];

  const load = useCallback(async (crumb: Crumb, more?: string) => {
    const mine = ++request.current;
    setLoading(true);
    setError(null);
    if (!more) { setItems([]); setCursor(null); setNotice(null); }
    try {
      const page = await api.browse(connection.id, crumb.location, crumb.query, more);
      if (mine !== request.current) return;
      setItems(prev => (more ? [...prev, ...page.items] : page.items));
      setCursor(page.cursor ?? null);
      setNotice(page.notice ?? null);
    } catch (e) {
      if (mine !== request.current) return;
      setError(e instanceof Error ? e.message : 'Could not list this location.');
    } finally {
      if (mine === request.current) setLoading(false);
    }
  }, [connection.id]);

  // The top level is the list of places; open the first (My files / My Drive) straight away.
  useEffect(() => {
    void (async () => {
      try {
        const top = await api.browse(connection.id, '');
        setPlaces(top.items);
        const first = top.items.find(x => x.location);
        if (first?.location) open({ name: first.name, location: first.location }, true);
      } catch (e) {
        setError(e instanceof Error ? e.message : 'Could not reach the connected account.');
        setLoading(false);
      }
    })();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [connection.id]);

  const open = (crumb: Crumb, fresh = false) => {
    setTrail(prev => (fresh ? [crumb] : [...prev, crumb]));
    setSearch('');
    void load(crumb);
  };

  const jump = (index: number) => {
    const crumb = trail[index];
    setTrail(trail.slice(0, index + 1));
    void load(crumb);
  };

  const runSearch = () => {
    const q = search.trim();
    if (!q || !here) return;
    const base = here.query ? trail[trail.length - 2] : here;
    const crumb = { name: `Results for “${q}”`, location: base?.location ?? '', query: q };
    setTrail(prev => [...(here.query ? prev.slice(0, -1) : prev), crumb]);
    void load(crumb);
  };

  const toggle = (s: BrowseSelection) => setPicked(prev => {
    const next = new Map(prev);
    if (next.has(keyOf(s))) next.delete(keyOf(s)); else next.set(keyOf(s), s);
    return next;
  });

  const selectable = items.filter(x => x.selection);
  const allHere = selectable.length > 0 && selectable.every(x => picked.has(keyOf(x.selection!)));
  const toggleAll = () => setPicked(prev => {
    const next = new Map(prev);
    for (const x of selectable) {
      if (allHere) next.delete(keyOf(x.selection!)); else next.set(keyOf(x.selection!), x.selection!);
    }
    return next;
  });

  const chosen = [...picked.values()];
  const folders = chosen.filter(x => x.folder).length;
  const files = chosen.length - folders;

  return (
    <Modal
      title={google ? 'Choose from Google Drive' : 'Choose from SharePoint / OneDrive'}
      subtitle={connection.account ? `Signed in as ${connection.account}` : undefined}
      width={920}
      onClose={onClose}
      footer={<>
        <span style={{ flex: 1, fontSize: 13, color: c.muted, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {chosen.length === 0
            ? 'Tick folders or files. A folder brings in everything inside it, including subfolders.'
            : `${folders ? `${folders} folder${folders === 1 ? '' : 's'}` : ''}${folders && files ? ' and ' : ''}${files ? `${files} file${files === 1 ? '' : 's'}` : ''} selected: ${chosen.map(x => x.name).join(', ')}`}
        </span>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="primary" disabled={chosen.length === 0} onClick={() => onPick(chosen)}>
          Add {chosen.length || ''}
        </Button>
      </>}
    >
      <div style={{ display: 'grid', gridTemplateColumns: '190px 1fr', gap: 16, height: 'min(520px, 62vh)' }}>
        {/* Places */}
        <nav style={{ display: 'flex', flexDirection: 'column', gap: 2, borderRight: `1px solid ${c.rule}`, paddingRight: 12 }}>
          {places.map(p => {
            const active = trail[0]?.location === p.location;
            return (
              <button
                key={p.location ?? p.name}
                onClick={() => p.location && open({ name: p.name, location: p.location }, true)}
                title={p.detail ?? undefined}
                style={{
                  display: 'flex', alignItems: 'center', gap: 8, padding: '8px 10px', borderRadius: 8,
                  border: 0, cursor: 'pointer', textAlign: 'left', fontFamily: 'inherit', fontSize: 14,
                  background: active ? c.active : 'transparent', color: c.ink, fontWeight: active ? 600 : 400,
                }}
              >
                <span>{ICON[p.kind] ?? '📁'}</span>{p.name}
              </button>
            );
          })}
        </nav>

        {/* Location */}
        <div style={{ display: 'flex', flexDirection: 'column', gap: 10, minWidth: 0 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
            <div style={{ flex: 1, minWidth: 0, display: 'flex', alignItems: 'center', gap: 4, fontSize: 14, flexWrap: 'wrap' }}>
              {trail.map((crumb, i) => (
                <span key={`${crumb.location}-${i}`} style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
                  {i > 0 && <span style={{ color: c.faint }}>›</span>}
                  <button
                    onClick={() => i < trail.length - 1 && jump(i)}
                    style={{
                      border: 0, background: 'transparent', padding: '2px 4px', fontFamily: 'inherit', fontSize: 14,
                      cursor: i < trail.length - 1 ? 'pointer' : 'default',
                      color: i < trail.length - 1 ? c.accent : c.ink, fontWeight: i === trail.length - 1 ? 600 : 400,
                    }}
                  >{crumb.name}</button>
                </span>
              ))}
            </div>
            <Input
              value={search}
              placeholder={google ? 'Search Drive' : 'Search files'}
              onChange={e => setSearch(e.target.value)}
              onKeyDown={e => { if (e.key === 'Enter') runSearch(); }}
              style={{ maxWidth: 220, height: 34 }}
            />
          </div>

          <div style={{ flex: 1, overflow: 'auto', border: `1px solid ${c.border}`, borderRadius: 10 }}>
            <div style={{
              display: 'grid', gridTemplateColumns: '36px 1fr 150px 90px', alignItems: 'center',
              padding: '8px 10px', fontSize: 12, color: c.muted, borderBottom: `1px solid ${c.rule}`,
              position: 'sticky', top: 0, background: c.surface,
            }}>
              <input type="checkbox" checked={allHere} disabled={selectable.length === 0} onChange={toggleAll} aria-label="Select all here" />
              <span>Name</span><span>Modified</span><span style={{ textAlign: 'right' }}>Size</span>
            </div>

            {items.map(item => {
              const s = item.selection;
              const isPicked = s ? picked.has(keyOf(s)) : false;
              return (
                <div
                  key={`${item.location ?? ''}-${s ? keyOf(s) : item.name}`}
                  onDoubleClick={() => item.location && open({ name: item.name, location: item.location })}
                  style={{
                    display: 'grid', gridTemplateColumns: '36px 1fr 150px 90px', alignItems: 'center',
                    padding: '7px 10px', fontSize: 14, borderBottom: `1px solid ${c.ruleSoft}`,
                    background: isPicked ? '#EEF2FF' : undefined,
                  }}
                >
                  <span>{s && <input type="checkbox" checked={isPicked} onChange={() => toggle(s)} aria-label={`Select ${item.name}`} />}</span>
                  <span style={{ display: 'flex', alignItems: 'center', gap: 8, minWidth: 0 }}>
                    <span>{ICON[item.kind] ?? '📄'}</span>
                    {item.location ? (
                      <button
                        onClick={() => open({ name: item.name, location: item.location! })}
                        style={{
                          border: 0, background: 'transparent', padding: 0, cursor: 'pointer', fontFamily: 'inherit',
                          fontSize: 14, color: c.ink, textAlign: 'left', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                        }}
                      >{item.name}</button>
                    ) : (
                      <span
                        onClick={() => s && toggle(s)}
                        style={{ cursor: s ? 'pointer' : 'default', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}
                      >{item.name}</span>
                    )}
                    {item.detail && <span style={{ fontSize: 12, color: c.dim, whiteSpace: 'nowrap' }}>{item.detail}</span>}
                  </span>
                  <span style={{ fontSize: 12, color: c.muted }}>{item.modifiedAt ? formatRelative(item.modifiedAt) : ''}</span>
                  <span style={{ fontSize: 12, color: c.muted, textAlign: 'right' }}>{item.size != null ? formatBytes(item.size) : ''}</span>
                </div>
              );
            })}

            {loading && <div style={{ padding: 16, fontSize: 13, color: c.muted }}>Loading…</div>}
            {!loading && error && <div style={{ padding: 16, fontSize: 13, color: '#B13A26' }}>{error}</div>}
            {!loading && !error && items.length === 0 && (
              <div style={{ padding: 16, fontSize: 13, color: c.muted }}>{notice ?? 'Nothing here.'}</div>
            )}
            {!loading && cursor && here && (
              <div style={{ padding: 10, textAlign: 'center' }}>
                <Button onClick={() => void load(here, cursor)} style={{ height: 32 }}>Load more</Button>
              </div>
            )}
          </div>
        </div>
      </div>
    </Modal>
  );
}
