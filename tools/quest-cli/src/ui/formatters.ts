import pc from "picocolors";

export function humanBytes(bytes: number): string {
  if (bytes === 0) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  const i = Math.floor(Math.log(bytes) / Math.log(1024));
  const value = bytes / Math.pow(1024, i);
  return `${value.toFixed(i === 0 ? 0 : 1)} ${units[i]}`;
}

export function storageBar(usedPercent: number, width = 20): string {
  const filled = Math.round((usedPercent / 100) * width);
  const empty = width - filled;
  const bar = "█".repeat(filled) + "░".repeat(empty);
  const color = usedPercent > 85 ? pc.red : usedPercent > 70 ? pc.yellow : pc.green;
  return color(bar) + ` ${usedPercent}% used`;
}

export function batteryIndicator(level: number, charging: boolean): string {
  const icon = charging ? "⚡" : level > 20 ? "🔋" : "🪫";
  const color = level > 50 ? pc.green : level > 20 ? pc.yellow : pc.red;
  return `${icon} ${color(`${level}%`)}`;
}

export function sessionDateStr(name: string): string {
  // Session format: YYYYMMDD_HHMMSS
  const match = name.match(/^(\d{4})(\d{2})(\d{2})_(\d{2})(\d{2})(\d{2})$/);
  if (!match) return name;
  const [, y, m, d, h, min, s] = match;
  return `${y}-${m}-${d} ${h}:${min}:${s}`;
}

export function syncStatusLabel(location: "device" | "local" | "both"): string {
  switch (location) {
    case "device":
      return pc.cyan("device-only");
    case "local":
      return pc.dim("local-only");
    case "both":
      return pc.green("synced");
  }
}

export function padRight(str: string, len: number): string {
  // Strip ANSI codes for length calculation
  const stripped = str.replace(/\x1b\[[0-9;]*m/g, "");
  const pad = Math.max(0, len - stripped.length);
  return str + " ".repeat(pad);
}

export function logLevelColor(level: string): (s: string) => string {
  switch (level) {
    case "V":
      return pc.dim;
    case "D":
      return pc.dim;
    case "I":
      return (s: string) => s; // default
    case "W":
      return pc.yellow;
    case "E":
      return pc.red;
    case "F":
      return pc.red;
    default:
      return (s: string) => s;
  }
}

export function tableRow(columns: string[], widths: number[]): string {
  return columns.map((col, i) => padRight(col, widths[i])).join("  ");
}

export function divider(char = "─", width = 60): string {
  return pc.dim(char.repeat(width));
}
