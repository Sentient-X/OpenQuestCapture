import { adbShellSync } from "../adb/adb";
import { DEVICE_FILES_DIR, SESSION_REGEX } from "../constants";
import type { SessionFile } from "../types";

/** List session directories on the device. */
export function listDeviceSessions(): string[] {
  try {
    const output = adbShellSync(`ls -1 ${DEVICE_FILES_DIR}`);
    return output
      .split("\n")
      .map((l) => l.trim().replace(/\r/g, ""))
      .filter((l) => SESSION_REGEX.test(l))
      .sort();
  } catch {
    return [];
  }
}

/** Get the total size of a session directory on the device (in bytes). */
export function getDeviceSessionSize(sessionName: string): number {
  try {
    const output = adbShellSync(`du -s ${DEVICE_FILES_DIR}/${sessionName}`);
    const match = output.match(/^(\d+)/);
    // du returns size in 1K blocks on Android
    return match ? parseInt(match[1], 10) * 1024 : 0;
  } catch {
    return 0;
  }
}

/** List files inside a session directory on the device. */
export function listDeviceSessionFiles(sessionName: string): SessionFile[] {
  try {
    const output = adbShellSync(`ls -la ${DEVICE_FILES_DIR}/${sessionName}`);
    const files: SessionFile[] = [];
    for (const line of output.split("\n")) {
      // -rw-rw---- ... size date name
      const parts = line.trim().split(/\s+/);
      if (parts.length >= 7 && parts[0].startsWith("-")) {
        const size = parseInt(parts[4], 10);
        const name = parts.slice(7).join(" ");
        if (name && !isNaN(size)) {
          files.push({ name, size });
        }
      }
    }
    return files;
  } catch {
    return [];
  }
}

/** Delete a session directory from the device. */
export function deleteDeviceSession(sessionName: string): boolean {
  try {
    adbShellSync(`rm -rf ${DEVICE_FILES_DIR}/${sessionName}`);
    return true;
  } catch {
    return false;
  }
}

/** Get sizes for multiple sessions in one batch. */
export function getDeviceSessionSizes(sessions: string[]): Map<string, number> {
  const sizes = new Map<string, number>();
  for (const s of sessions) {
    sizes.set(s, getDeviceSessionSize(s));
  }
  return sizes;
}
