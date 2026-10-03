import { useState } from 'react';
import { api } from '../lib/api';
import type { SourceConnectionDto } from '../lib/types';
import { c } from '../theme';
import { Button, Modal } from './ui';

/**
 * Confirms removing a connected account. Dochub drops the stored access (and, for
 * GitHub, withdraws it at GitHub); imported documents stay. The same source can
 * be connected again straight afterwards.
 */
export default function RemoveConnection({ connection, onClose, onRemoved }: {
  connection: SourceConnectionDto;
  onClose: () => void;
  onRemoved: () => void;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const remove = async () => {
    setBusy(true);
    setError(null);
    try {
      await api.disconnect(connection.id);
      onRemoved();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not remove the connection.');
      setBusy(false);
    }
  };

  return (
    <Modal
      title={`Remove ${connection.displayName}?`}
      subtitle={connection.account ? `Connected as ${connection.account}` : undefined}
      width={460}
      onClose={onClose}
      footer={<>
        <span style={{ flex: 1, fontSize: 13, color: '#B13A26' }}>{error}</span>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="primary" disabled={busy} onClick={() => void remove()}>
          {busy ? 'Removing…' : 'Remove'}
        </Button>
      </>}
    >
      <div style={{ fontSize: 14, color: c.soft, lineHeight: 1.55 }}>
        Dochub deletes the stored access{connection.sourceType === 'GitHub' ? ' and withdraws it at GitHub' : ''}.
        Documents already imported stay where they are, and you can connect again at any time.
        {connection.scheduleCount > 0 && (
          <div style={{ marginTop: 10, color: '#94600F' }}>
            {connection.scheduleCount} scheduled sync{connection.scheduleCount === 1 ? ' runs' : 's run'} under this
            connection and will fail until you connect {connection.displayName} again.
          </div>
        )}
      </div>
    </Modal>
  );
}
