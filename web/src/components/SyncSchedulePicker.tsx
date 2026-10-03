import { c } from '../theme';
import type { SyncFrequency, SyncScheduleRequest } from '../lib/types';
import { Field, Input, Select } from './ui';

export type UploadMode = 'once' | 'recurring';

const DAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];

/** The browser's own zone, so "09:00" means 09:00 where the person setting it sits. */
export const localTimeZone = () => {
  try { return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'; }
  catch { return 'UTC'; }
};

export interface ScheduleDraft {
  frequency: SyncFrequency;
  timeOfDay: string;
  dayOfWeek: number;
  dayOfMonth: number;
}

export const emptyDraft = (): ScheduleDraft => ({
  frequency: 'Daily', timeOfDay: '09:00', dayOfWeek: 1, dayOfMonth: 1,
});

export const toRequest = (draft: ScheduleDraft): SyncScheduleRequest => ({
  frequency: draft.frequency,
  timeOfDay: draft.timeOfDay,
  timeZoneId: localTimeZone(),
  dayOfWeek: draft.frequency === 'Weekly' ? draft.dayOfWeek : undefined,
  dayOfMonth: draft.frequency === 'Monthly' ? draft.dayOfMonth : undefined,
});

export const describe = (draft: ScheduleDraft) => {
  const zone = localTimeZone();
  if (draft.frequency === 'Daily') return `Every day at ${draft.timeOfDay} (${zone})`;
  if (draft.frequency === 'Weekly') return `Every ${DAYS[draft.dayOfWeek]} at ${draft.timeOfDay} (${zone})`;
  return `Day ${draft.dayOfMonth} of each month at ${draft.timeOfDay} (${zone})`;
};

/**
 * The two ways to bring a source in: once, or once and then on a cadence.
 * Recurring is only offered for SharePoint, Google Drive and GitHub — the sources whose
 * folders can be re-listed later. `disabledReason` explains when it can't be.
 */
export default function SyncSchedulePicker({
  mode, onModeChange, draft, onDraftChange, disabledReason,
}: {
  mode: UploadMode;
  onModeChange: (mode: UploadMode) => void;
  draft: ScheduleDraft;
  onDraftChange: (draft: ScheduleDraft) => void;
  disabledReason?: string;
}) {
  const locked = Boolean(disabledReason);
  const active = locked ? 'once' : mode;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
      <div style={{
        fontSize: 12, fontWeight: 600, color: c.dim,
        letterSpacing: '.04em', textTransform: 'uppercase',
      }}>How should this source be kept?</div>

      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit,minmax(230px,1fr))', gap: 10 }}>
        <Option
          selected={active === 'once'}
          disabled={false}
          title="Import once"
          body="Bring the documents in now. Nothing changes again until someone imports them.">
          {null}
        </Option>
        <Option
          selected={active === 'recurring'}
          disabled={locked}
          title="Import and keep in sync"
          body={disabledReason ?? 'Re-read this location on a schedule and refresh the vectors for files that changed.'}
          onSelect={() => onModeChange('recurring')}>
          {null}
        </Option>
      </div>

      {/* The clickable layer sits over the cards so the whole card is a target. */}
      <div style={{ display: 'none' }} />

      {active === 'recurring' && !locked && (
        <div style={{
          border: `1px solid ${c.border}`, borderRadius: 12, padding: 14,
          display: 'flex', flexDirection: 'column', gap: 12, background: c.accentTint,
        }}>
          <div style={{
            display: 'grid',
            gridTemplateColumns: draft.frequency === 'Daily' ? '1fr 1fr' : '1fr 1fr 1fr',
            gap: 10,
          }}>
            <Field label="Frequency">
              <Select
                value={draft.frequency}
                onChange={e => onDraftChange({ ...draft, frequency: e.target.value as SyncFrequency })}
              >
                <option value="Daily">Daily</option>
                <option value="Weekly">Weekly</option>
                <option value="Monthly">Monthly</option>
              </Select>
            </Field>

            {draft.frequency === 'Weekly' && (
              <Field label="Day">
                <Select
                  value={draft.dayOfWeek}
                  onChange={e => onDraftChange({ ...draft, dayOfWeek: Number(e.target.value) })}
                >
                  {DAYS.map((day, index) => <option key={day} value={index}>{day}</option>)}
                </Select>
              </Field>
            )}

            {draft.frequency === 'Monthly' && (
              <Field label="Day of month">
                <Select
                  value={draft.dayOfMonth}
                  onChange={e => onDraftChange({ ...draft, dayOfMonth: Number(e.target.value) })}
                >
                  {/* Capped at 28 so the run never skips a short month. */}
                  {Array.from({ length: 28 }, (_, i) => i + 1).map(day => (
                    <option key={day} value={day}>{day}</option>
                  ))}
                </Select>
              </Field>
            )}

            <Field label="Time of day">
              <Input
                type="time"
                value={draft.timeOfDay}
                onChange={e => onDraftChange({ ...draft, timeOfDay: e.target.value })}
              />
            </Field>
          </div>

          <div style={{ fontSize: 12, color: c.muted, lineHeight: 1.5 }}>
            {describe(draft)}. Files are matched by name — a changed file updates the
            document you already have rather than adding a duplicate, and unchanged
            files are left alone.
          </div>
        </div>
      )}
    </div>
  );

  function Option({
    selected, disabled, title, body, onSelect,
  }: {
    selected: boolean; disabled: boolean; title: string; body: string;
    onSelect?: () => void; children?: React.ReactNode;
  }) {
    return (
      <button
        type="button"
        disabled={disabled}
        onClick={() => (onSelect ?? (() => onModeChange('once')))()}
        style={{
          textAlign: 'left', borderRadius: 12, padding: '12px 14px', cursor: disabled ? 'not-allowed' : 'pointer',
          background: selected ? c.accentTint : c.surface,
          border: selected ? `2px solid ${c.accent}` : `1px solid ${c.border}`,
          display: 'flex', gap: 10, alignItems: 'flex-start', fontFamily: 'inherit',
          opacity: disabled ? 0.65 : 1,
        }}
      >
        <span style={{
          width: 16, height: 16, borderRadius: '50%', flex: 'none', marginTop: 2,
          border: `2px solid ${selected ? c.accent : c.borderStrong}`,
          background: selected ? c.accent : 'transparent',
          boxShadow: selected ? `inset 0 0 0 3px ${c.surface}` : 'none',
        }} />
        <span style={{ display: 'flex', flexDirection: 'column', gap: 3, minWidth: 0 }}>
          <span style={{ fontSize: 14, fontWeight: 600, color: c.ink }}>{title}</span>
          <span style={{ fontSize: 12, color: c.muted, lineHeight: 1.45 }}>{body}</span>
        </span>
      </button>
    );
  }
}
