/** Design tokens lifted from the Dochub UI design. */
export const c = {
  canvas: '#F6F6F3',
  surface: '#fff',
  sidebar: '#FBFBF9',
  ink: '#16181D',
  inkHover: '#2B2E35',
  body: '#2B2E35',
  soft: '#3D434D',
  muted: '#6B6F76',
  dim: '#8A8E95',
  faint: '#A0A4AB',
  disabled: '#B9BDC6',
  border: '#E6E6E1',
  borderStrong: '#DADAD4',
  rule: '#EEEEE9',
  ruleSoft: '#F1F1ED',
  hover: '#F0F0EC',
  active: '#EDEDE8',
  track: '#EEEEE9',
  accent: '#3552D6',
  accentHover: '#2440B8',
  accentTint: '#FAFBFF',
} as const;

export const mono = "'Geist Mono', ui-monospace, SFMono-Regular, Menlo, monospace";

/** Source-system chips: mark, background and foreground. */
export const SRC: Record<string, { label: string; mark: string; bg: string; fg: string }> = {
  GitHub: { label: 'GitHub', mark: 'GH', bg: '#16181D', fg: '#fff' },
  SharePoint: { label: 'SharePoint', mark: 'SP', bg: '#E3F1EE', fg: '#0B6A5A' },
  GoogleDrive: { label: 'Google Drive', mark: 'GD', bg: '#FDF1DC', fg: '#935700' },
  Local: { label: 'Local files', mark: 'LF', bg: '#ECEEF2', fg: '#3D434D' },
  AzureDevOps: { label: 'Azure DevOps', mark: 'AZ', bg: '#E3EEFB', fg: '#0B5CAD' },
  Confluence: { label: 'Confluence', mark: 'CF', bg: '#E8EEFB', fg: '#1D4FB8' },
  Jira: { label: 'Jira', mark: 'JR', bg: '#E6F0FF', fg: '#0C66E4' },
};

export const srcOf = (key?: string) => SRC[key ?? 'Local'] ?? SRC.Local;

/** Status pills, shared by documents, artifacts and submitted sources. */
export const ST: Record<string, { bg: string; fg: string; dot: string }> = {
  Indexed: { bg: '#E7F4EC', fg: '#1F7A4F', dot: '#2E9A64' },
  Processed: { bg: '#E7F4EC', fg: '#1F7A4F', dot: '#2E9A64' },
  Pending: { bg: '#FBF1DF', fg: '#94600F', dot: '#D08A1E' },
  'Request Upload': { bg: '#FBF1DF', fg: '#94600F', dot: '#D08A1E' },
  Uploading: { bg: '#E9EEFC', fg: '#3552D6', dot: '#3552D6' },
  Uploaded: { bg: '#E9EEFC', fg: '#3552D6', dot: '#3552D6' },
  Processing: { bg: '#E9EEFC', fg: '#3552D6', dot: '#3552D6' },
  Failed: { bg: '#FBEAE6', fg: '#B13A26', dot: '#C2412D' },
  'Completed with errors': { bg: '#FBF1DF', fg: '#94600F', dot: '#D08A1E' },
  Skipped: { bg: '#F1F1ED', fg: '#6B6F76', dot: '#B0B3BA' },
  Empty: { bg: '#F1F1ED', fg: '#6B6F76', dot: '#B0B3BA' },
  Cancelled: { bg: '#F1F1ED', fg: '#6B6F76', dot: '#B0B3BA' },
};

export const stOf = (key?: string) => ST[key ?? 'Pending'] ?? ST.Pending;
