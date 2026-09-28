import { api } from './api';
import type { SourceConnectionDto } from './types';

const POPUP = 'width=520,height=680,menubar=no,toolbar=no,location=yes';

/**
 * Runs the provider's consent flow in a popup and returns the stored connection.
 *
 * A popup rather than a full-page redirect on purpose: the Upload screen holds
 * the user's staged sources in memory, and navigating away would discard them.
 */
export async function connectWithProvider(sourceType: string): Promise<SourceConnectionDto> {
  // Opened synchronously, before any await — a window opened later is not a
  // direct result of the click and popup blockers reject it.
  const popup = window.open('about:blank', 'dochub-oauth', POPUP);
  if (!popup) {
    throw new Error('Your browser blocked the sign-in window. Allow popups for this site and try again.');
  }

  let start: Awaited<ReturnType<typeof api.startOAuth>>;
  try {
    start = await api.startOAuth(sourceType);
  } catch (error) {
    popup.close();
    throw error;
  }

  popup.location.replace(start.authorizeUrl);

  return new Promise<SourceConnectionDto>((resolve, reject) => {
    let settled = false;

    const finish = (fn: () => void) => {
      if (settled) return;
      settled = true;
      window.removeEventListener('message', onMessage);
      clearInterval(closedCheck);
      fn();
    };

    const onMessage = (event: MessageEvent) => {
      // Only this app's own callback page may complete the flow.
      if (event.origin !== window.location.origin) return;
      const data = event.data as { type?: string; connection?: SourceConnectionDto; error?: string };
      if (data?.type !== 'dochub:oauth') return;

      if (data.error) finish(() => reject(new Error(data.error)));
      else if (data.connection) finish(() => resolve(data.connection!));
    };

    window.addEventListener('message', onMessage);

    // The popup can be dismissed without ever reaching the callback.
    const closedCheck = setInterval(() => {
      if (popup.closed) {
        finish(() => reject(new Error('Sign-in was cancelled.')));
      }
    }, 500);
  });
}
