import * as p from "@clack/prompts";
import pc from "picocolors";
import { listDeviceSessions, getDeviceSessionSize, deleteDeviceSession } from "../sessions/device-sessions";
import { localSessionExists } from "../sessions/local-sessions";
import { pullSessions } from "../sessions/sync";
import { humanBytes, sessionDateStr } from "../ui/formatters";

export async function pullSessionsCommand(): Promise<void> {
  const s = p.spinner();
  s.start("Checking device sessions...");

  const allDevice = listDeviceSessions();
  const newSessions = allDevice.filter((name) => !localSessionExists(name));

  s.stop(`Found ${newSessions.length} new session(s) on device.`);

  if (newSessions.length === 0) {
    p.log.info("All device sessions are already synced locally.");
    return;
  }

  // Build selection options with sizes
  const options = newSessions.map((name) => {
    const size = getDeviceSessionSize(name);
    return {
      value: name,
      label: `${name}  ${pc.dim(sessionDateStr(name))}  ${pc.cyan(humanBytes(size))}`,
    };
  });

  const selected = await p.multiselect({
    message: "Select sessions to pull:",
    options,
    required: true,
  });

  if (p.isCancel(selected)) return;

  const totalSize = (selected as string[]).reduce(
    (sum, name) => sum + getDeviceSessionSize(name),
    0
  );
  p.log.info(`Pulling ${(selected as string[]).length} session(s), ~${humanBytes(totalSize)}`);

  const pullSpinner = p.spinner();
  let currentSession = "";

  pullSpinner.start("Starting pull...");

  const result = await pullSessions(selected as string[], (progress) => {
    if (progress.status === "pulling") {
      currentSession = progress.session;
      pullSpinner.message(`Pulling ${progress.session}...`);
    } else if (progress.status === "done") {
      pullSpinner.message(`Pulled ${progress.session} ✓`);
    } else if (progress.status === "error") {
      pullSpinner.message(`Failed ${progress.session}: ${progress.message}`);
    }
  });

  pullSpinner.stop(
    `Done: ${result.succeeded.length} pulled, ${result.failed.length} failed.`
  );

  if (result.failed.length > 0) {
    p.log.warn(`Failed: ${result.failed.join(", ")}`);
  }

  if (result.succeeded.length > 0) {
    const deleteAfter = await p.confirm({
      message: "Delete pulled sessions from device?",
      initialValue: false,
    });

    if (!p.isCancel(deleteAfter) && deleteAfter) {
      const delSpinner = p.spinner();
      delSpinner.start("Deleting from device...");

      let deleted = 0;
      for (const name of result.succeeded) {
        if (deleteDeviceSession(name)) deleted++;
      }

      delSpinner.stop(`Deleted ${deleted} session(s) from device.`);
    }
  }
}
