import * as p from "@clack/prompts";
import pc from "picocolors";
import { spawnSync } from "bun";
import { adbSpawn, requireDevice } from "../adb/adb";
import { LOGCAT_TAGS } from "../constants";
import { logLevelColor } from "../ui/formatters";

const LOG_LINE_REGEX = /^[0-9-]+\s+[0-9:.]+\s+\d+\s+\d+\s+([VDIWEF])\s+(.+)$/;

function formatLogLine(line: string): string | null {
  const match = line.match(LOG_LINE_REGEX);
  if (match) {
    const [, level, rest] = match;
    const colorFn = logLevelColor(level);
    return colorFn(`${pc.bold(level)} ${rest}`);
  }
  // Fallback: show line as-is if it has content
  const trimmed = line.trim();
  return trimmed.length > 0 ? pc.dim(trimmed) : null;
}

export async function liveMonitorCommand(): Promise<void> {
  try {
    requireDevice();
  } catch (e: any) {
    p.log.error(e.message);
    return;
  }

  p.log.info(`Streaming logcat (${LOGCAT_TAGS.join(", ")})`);
  p.log.info(pc.dim("Press Ctrl+C to return to menu."));
  console.log();

  // Clear logcat buffer first
  spawnSync(["adb", "logcat", "-c"]);

  const proc = adbSpawn(["logcat", "-v", "threadtime", ...LOGCAT_TAGS, "*:S"], {
    onStdout: (line) => {
      const formatted = formatLogLine(line);
      if (formatted) console.log(formatted);
    },
    onStderr: (line) => {
      if (line.trim()) console.error(pc.red(line));
    },
  });

  // Wait for Ctrl+C
  await new Promise<void>((resolve) => {
    const handler = () => {
      process.removeListener("SIGINT", handler);
      resolve();
    };
    process.on("SIGINT", handler);
  });

  proc.kill();
  await proc.exited;
  console.log();
  p.log.info("Logcat stopped.");
}
