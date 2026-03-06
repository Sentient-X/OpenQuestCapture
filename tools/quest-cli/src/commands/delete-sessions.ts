import * as p from "@clack/prompts";
import pc from "picocolors";
import {
  listDeviceSessions,
  getDeviceSessionSize,
  deleteDeviceSession,
} from "../sessions/device-sessions";
import {
  listLocalSessions,
  getLocalSessionSize,
  deleteLocalSession,
} from "../sessions/local-sessions";
import { humanBytes, sessionDateStr } from "../ui/formatters";

export async function deleteSessionsCommand(): Promise<void> {
  const target = await p.select({
    message: "Delete from where?",
    options: [
      { value: "device", label: "Device" },
      { value: "local", label: "Local" },
      { value: "both", label: "Both" },
    ],
  });

  if (p.isCancel(target)) return;

  const includeDevice = target === "device" || target === "both";
  const includeLocal = target === "local" || target === "both";

  // Gather sessions based on target
  const deviceSessions = includeDevice ? listDeviceSessions() : [];
  const localSessions = includeLocal ? listLocalSessions() : [];
  const allNames = [...new Set([...deviceSessions, ...localSessions])].sort();

  if (allNames.length === 0) {
    p.log.info("No sessions found.");
    return;
  }

  const options = allNames.map((name) => {
    let size = 0;
    const locations: string[] = [];
    if (deviceSessions.includes(name)) {
      size += getDeviceSessionSize(name);
      locations.push("device");
    }
    if (localSessions.includes(name)) {
      size += getLocalSessionSize(name);
      locations.push("local");
    }
    const locStr = pc.dim(`[${locations.join("+")}]`);
    return {
      value: name,
      label: `${name}  ${pc.dim(sessionDateStr(name))}  ${pc.cyan(humanBytes(size))}  ${locStr}`,
    };
  });

  const selected = await p.multiselect({
    message: "Select sessions to delete:",
    options,
    required: true,
  });

  if (p.isCancel(selected)) return;

  const toDelete = selected as string[];

  // Estimate space freed
  let spaceFreed = 0;
  for (const name of toDelete) {
    if (includeDevice && deviceSessions.includes(name))
      spaceFreed += getDeviceSessionSize(name);
    if (includeLocal && localSessions.includes(name))
      spaceFreed += getLocalSessionSize(name);
  }

  const confirmed = await p.confirm({
    message: `Delete ${toDelete.length} session(s)? (~${humanBytes(spaceFreed)} freed)`,
    initialValue: false,
  });

  if (p.isCancel(confirmed) || !confirmed) {
    p.log.info("Cancelled.");
    return;
  }

  const s = p.spinner();
  s.start("Deleting...");

  let deletedCount = 0;
  let failedCount = 0;

  for (const name of toDelete) {
    let ok = true;
    if (includeDevice && deviceSessions.includes(name)) {
      ok = deleteDeviceSession(name) && ok;
    }
    if (includeLocal && localSessions.includes(name)) {
      ok = deleteLocalSession(name) && ok;
    }
    if (ok) deletedCount++;
    else failedCount++;
  }

  s.stop(`Deleted ${deletedCount}, failed ${failedCount}.`);
}
