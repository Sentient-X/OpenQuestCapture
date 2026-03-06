import { readdirSync, statSync, rmSync, existsSync } from "fs";
import { join } from "path";
import { SESSION_REGEX, LOCAL_RECORDINGS_DIR } from "../constants";

function getRecordingsDir(): string {
  // Resolve relative to project root (two levels up from tools/quest-cli/)
  return join(import.meta.dir, "..", "..", "..", "..", LOCAL_RECORDINGS_DIR);
}

/** List session directories in the local recordings folder. */
export function listLocalSessions(): string[] {
  const dir = getRecordingsDir();
  if (!existsSync(dir)) return [];

  try {
    return readdirSync(dir)
      .filter((name) => {
        const fullPath = join(dir, name);
        return SESSION_REGEX.test(name) && statSync(fullPath).isDirectory();
      })
      .sort();
  } catch {
    return [];
  }
}

/** Get total size of a local session directory (recursive). */
export function getLocalSessionSize(sessionName: string): number {
  const dir = join(getRecordingsDir(), sessionName);
  if (!existsSync(dir)) return 0;

  let total = 0;
  function walk(dirPath: string) {
    const entries = readdirSync(dirPath, { withFileTypes: true });
    for (const entry of entries) {
      const fullPath = join(dirPath, entry.name);
      if (entry.isDirectory()) {
        walk(fullPath);
      } else {
        total += statSync(fullPath).size;
      }
    }
  }
  walk(dir);
  return total;
}

/** Delete a local session directory. */
export function deleteLocalSession(sessionName: string): boolean {
  const dir = join(getRecordingsDir(), sessionName);
  if (!existsSync(dir)) return false;
  try {
    rmSync(dir, { recursive: true, force: true });
    return true;
  } catch {
    return false;
  }
}

/** Check if a session exists locally. */
export function localSessionExists(sessionName: string): boolean {
  return existsSync(join(getRecordingsDir(), sessionName));
}

/** Get the local recordings directory path. */
export function getLocalRecordingsPath(): string {
  return getRecordingsDir();
}
