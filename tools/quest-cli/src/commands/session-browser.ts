import * as p from "@clack/prompts";
import pc from "picocolors";
import { listDeviceSessions, getDeviceSessionSize } from "../sessions/device-sessions";
import { listLocalSessions, getLocalSessionSize } from "../sessions/local-sessions";
import {
  humanBytes,
  sessionDateStr,
  syncStatusLabel,
  tableRow,
  divider,
} from "../ui/formatters";
import type { SessionInfo } from "../types";

function buildSessionList(): SessionInfo[] {
  const deviceSessions = new Set(listDeviceSessions());
  const localSessions = new Set(listLocalSessions());

  const allNames = new Set([...deviceSessions, ...localSessions]);
  const sessions: SessionInfo[] = [];

  for (const name of allNames) {
    const onDevice = deviceSessions.has(name);
    const onLocal = localSessions.has(name);
    const location = onDevice && onLocal ? "both" : onDevice ? "device" : "local";

    let size = 0;
    if (onLocal) {
      size = getLocalSessionSize(name);
    } else if (onDevice) {
      size = getDeviceSessionSize(name);
    }

    sessions.push({
      name,
      size,
      dateStr: sessionDateStr(name),
      location,
    });
  }

  return sessions.sort((a, b) => b.name.localeCompare(a.name));
}

export async function sessionBrowserCommand(): Promise<"pull" | null> {
  const s = p.spinner();
  s.start("Scanning sessions...");

  const sessions = buildSessionList();
  s.stop(`Found ${sessions.length} sessions.`);

  if (sessions.length === 0) {
    p.log.info("No sessions found on device or locally.");
    return null;
  }

  const widths = [18, 20, 10, 14];
  console.log();
  console.log(
    pc.dim(tableRow(["Name", "Date", "Size", "Status"], widths))
  );
  console.log(divider("─", 66));

  for (const sess of sessions) {
    console.log(
      tableRow(
        [sess.name, sess.dateStr, humanBytes(sess.size), syncStatusLabel(sess.location)],
        widths
      )
    );
  }
  console.log();

  const deviceOnly = sessions.filter((s) => s.location === "device");
  if (deviceOnly.length > 0) {
    const pullNow = await p.confirm({
      message: `${deviceOnly.length} device-only session(s). Pull them now?`,
      initialValue: false,
    });
    if (p.isCancel(pullNow)) return null;
    if (pullNow) return "pull";
  }

  return null;
}
