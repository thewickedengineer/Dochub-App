import type { CSSProperties, ReactNode } from 'react';
import { c, mono, srcOf, stOf } from '../theme';

export function SourceMark({ source, size = 22 }: { source: string; size?: number }) {
  const s = srcOf(source);
  return (
    <span style={{
      width: size, height: size, borderRadius: size / 4, display: 'grid', placeItems: 'center',
      fontSize: Math.max(8, size * 0.42), fontWeight: 700, flex: 'none',
      background: s.bg, color: s.fg,
    }}>{s.mark}</span>
  );
}

export function StatusPill({ status }: { status: string }) {
  const s = stOf(status);
  return (
    <span style={{
      justifySelf: 'start', fontSize: 12, fontWeight: 500, borderRadius: 99,
      padding: '3px 9px', whiteSpace: 'nowrap', background: s.bg, color: s.fg,
    }}>{status}</span>
  );
}

export function Dot({ color }: { color: string }) {
  return <span style={{ width: 8, height: 8, borderRadius: '50%', background: color, flex: 'none' }} />;
}

export function Bar({ percent, color = c.accent }: { percent: number; color?: string }) {
  return (
    <div style={{ flex: 1, height: 6, borderRadius: 99, background: c.track, overflow: 'hidden' }}>
      <div style={{ height: '100%', background: color, width: `${Math.min(100, Math.max(0, percent))}%`, transition: 'width .6s ease' }} />
    </div>
  );
}

export function Card({ children, style, pad = 18 }: { children: ReactNode; style?: CSSProperties; pad?: number }) {
  return (
    <div style={{
      background: c.surface, border: `1px solid ${c.border}`, borderRadius: 12,
      padding: pad, display: 'flex', flexDirection: 'column', gap: 12, ...style,
    }}>{children}</div>
  );
}

export function Button({
  children, onClick, variant = 'secondary', disabled, style, type = 'button',
}: {
  children: ReactNode; onClick?: () => void; disabled?: boolean; type?: 'button' | 'submit';
  variant?: 'primary' | 'secondary' | 'accent' | 'ghost'; style?: CSSProperties;
}) {
  const palette: Record<string, CSSProperties> = {
    primary: { background: c.ink, color: '#fff', border: 0 },
    accent: { background: c.accent, color: '#fff', border: 0 },
    secondary: { background: c.surface, color: c.ink, border: `1px solid ${c.borderStrong}` },
    ghost: { background: 'transparent', color: c.accent, border: 0, fontWeight: 500 },
  };
  return (
    <button
      type={type}
      onClick={onClick}
      disabled={disabled}
      style={{
        height: 38, padding: '0 16px', borderRadius: 9, fontSize: 14, fontWeight: 500,
        cursor: disabled ? 'not-allowed' : 'pointer', whiteSpace: 'nowrap',
        fontFamily: 'inherit', ...palette[variant],
        ...(disabled ? { background: c.track, color: c.dim, border: 0 } : {}),
        ...style,
      }}
    >{children}</button>
  );
}

export function Input(props: React.InputHTMLAttributes<HTMLInputElement>) {
  return (
    <input
      {...props}
      style={{
        height: 40, border: `1px solid ${c.borderStrong}`, borderRadius: 9, padding: '0 12px',
        fontSize: 14, outline: 0, color: c.ink, background: c.surface, fontFamily: 'inherit',
        width: '100%', ...props.style,
      }}
    />
  );
}

export function Select(props: React.SelectHTMLAttributes<HTMLSelectElement>) {
  return (
    <select
      {...props}
      style={{
        height: 40, border: `1px solid ${c.borderStrong}`, borderRadius: 9, padding: '0 10px',
        fontSize: 14, background: c.surface, color: c.ink, fontFamily: 'inherit',
        width: '100%', ...props.style,
      }}
    />
  );
}

export function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label style={{ display: 'flex', flexDirection: 'column', gap: 6, fontSize: 13, fontWeight: 500, minWidth: 0 }}>
      {label}
      {children}
    </label>
  );
}

export function Modal({
  title, subtitle, onClose, children, footer, width = 440,
}: {
  title: string; subtitle?: string; onClose: () => void;
  children: ReactNode; footer: ReactNode; width?: number;
}) {
  return (
    <div
      onClick={onClose}
      style={{
        position: 'fixed', inset: 0, background: 'rgba(22,24,29,.38)', zIndex: 50,
        display: 'grid', placeItems: 'center', padding: 24,
      }}
    >
      <div
        onClick={e => e.stopPropagation()}
        style={{
          width: '100%', maxWidth: width, background: c.surface, borderRadius: 16,
          boxShadow: '0 24px 64px rgba(22,24,29,.25)', overflow: 'hidden',
          animation: 'slideUp .18s ease', maxHeight: '90vh', display: 'flex', flexDirection: 'column',
        }}
      >
        <div style={{ padding: '20px 24px 4px', display: 'flex', alignItems: 'flex-start', gap: 12 }}>
          <div style={{ flex: 1, display: 'flex', flexDirection: 'column', gap: 4 }}>
            <div style={{ fontSize: 18, fontWeight: 600 }}>{title}</div>
            {subtitle && <div style={{ fontSize: 14, color: c.muted }}>{subtitle}</div>}
          </div>
          <button onClick={onClose} style={{ border: 0, background: 'transparent', fontSize: 20, cursor: 'pointer', color: c.muted }}>×</button>
        </div>
        <div style={{ padding: '16px 24px', display: 'flex', flexDirection: 'column', gap: 12, overflow: 'auto' }}>
          {children}
        </div>
        <div style={{
          padding: '14px 24px', borderTop: `1px solid ${c.rule}`, background: c.sidebar,
          display: 'flex', alignItems: 'center', gap: 10, flex: 'none',
        }}>{footer}</div>
      </div>
    </div>
  );
}

export function PageHeading({ title, subtitle, aside }: { title: string; subtitle: string; aside?: ReactNode }) {
  return (
    <div style={{ display: 'flex', alignItems: 'flex-end', gap: 16, flexWrap: 'wrap' }}>
      <div style={{ flex: 1, display: 'flex', flexDirection: 'column', gap: 4, minWidth: 260 }}>
        <div style={{ fontSize: 24, fontWeight: 600, letterSpacing: '-0.02em' }}>{title}</div>
        <div style={{ color: c.muted, fontSize: 14 }}>{subtitle}</div>
      </div>
      {aside}
    </div>
  );
}

export function Empty({ children }: { children: ReactNode }) {
  return <div style={{ padding: '40px 20px', textAlign: 'center', color: c.dim, fontSize: 14 }}>{children}</div>;
}

export function Mono({ children }: { children: ReactNode }) {
  return <span style={{ fontFamily: mono, fontSize: 13, color: c.soft }}>{children}</span>;
}
