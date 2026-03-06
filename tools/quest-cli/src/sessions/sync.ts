import { spawn } from "bun";
import { mkdirSync, existsSync } from "fs";
import { DEVICE_FILES_DIR } from "../constants";
import { getLocalRecordingsPath } from "./local-sessions";

export interface PullProgress {
  session: string;
  status: "pulling" | "done" | "error";
  message?: string;
}

/** Pull a single session from device to local recordings. */
export async function pullSession(
  sessionName: string,
  onProgress?: (progress: PullProgress) => void
): Promise<boolean> {
  const localDir = getLocalRecordingsPath();
  mkdirSync(localDir, { recursive: true });

  const remotePath = `${DEVICE_FILES_DIR}/${sessionName}`;

  onProgress?.({ session: sessionName, status: "pulling" });

  const proc = spawn(["adb", "pull", remotePath, localDir], {
    stdout: "pipe",
    stderr: "pipe",
  });

  const stderr = await new Response(proc.stderr).text();
  const exitCode = await proc.exited;

  if (exitCode !== 0) {
    onProgress?.({
      session: sessionName,
      status: "error",
      message: stderr.trim(),
    });
    return false;
  }

  // Verify the session was created locally
  const localPath = `${localDir}/${sessionName}`;
  if (!existsSync(localPath)) {
    onProgress?.({
      session: sessionName,
      status: "error",
      message: "Pull completed but directory not found locally",
    });
    return false;
  }

  onProgress?.({ session: sessionName, status: "done" });
  return true;
}

/** Pull multiple sessions sequentially with progress. */
export async function pullSessions(
  sessions: string[],
  onProgress?: (progress: PullProgress) => void
): Promise<{ succeeded: string[]; failed: string[] }> {
  const succeeded: string[] = [];
  const failed: string[] = [];

  for (const session of sessions) {
    const ok = await pullSession(session, onProgress);
    if (ok) {
      succeeded.push(session);
    } else {
      failed.push(session);
    }
  }

  return { succeeded, failed };
}
