import * as p from "@clack/prompts";
import pc from "picocolors";
import { spawn } from "bun";
import { adbSync, adb, requireDevice } from "../adb/adb";
import { launchApp, forceStopApp, isAppRunning } from "../adb/package-manager";
import { PACKAGE_NAME } from "../constants";

async function takeScreenshot(): Promise<void> {
  const timestamp = new Date().toISOString().replace(/[:.]/g, "-").slice(0, 19);
  const remotePath = `/sdcard/screenshot_${timestamp}.png`;
  const localPath = `screenshot_${timestamp}.png`;

  const s = p.spinner();
  s.start("Taking screenshot...");

  try {
    adbSync(["shell", "screencap", "-p", remotePath]);
    await adb(["pull", remotePath, localPath]);
    adbSync(["shell", "rm", remotePath]);
    s.stop(`Screenshot saved: ${localPath}`);
  } catch (e: any) {
    s.stop("Screenshot failed.");
    p.log.error(e.message);
  }
}

async function rebootDevice(): Promise<void> {
  const confirmed = await p.confirm({
    message: "Reboot the Quest? This will disconnect ADB.",
    initialValue: false,
  });
  if (p.isCancel(confirmed) || !confirmed) return;

  try {
    adbSync(["reboot"]);
    p.log.info("Reboot command sent. Device will disconnect.");
  } catch (e: any) {
    p.log.error(e.message);
  }
}

async function openShell(): Promise<void> {
  p.log.info(pc.dim("Opening ADB shell. Type 'exit' to return."));
  const proc = spawn(["adb", "shell"], {
    stdin: "inherit",
    stdout: "inherit",
    stderr: "inherit",
  });
  await proc.exited;
}

export async function quickActionsCommand(): Promise<void> {
  try {
    requireDevice();
  } catch (e: any) {
    p.log.error(e.message);
    return;
  }

  const running = isAppRunning();

  const action = await p.select({
    message: "Quick action:",
    options: [
      {
        value: "launch",
        label: `Launch app${running ? pc.dim(" (already running)") : ""}`,
      },
      { value: "stop", label: "Force stop app" },
      { value: "screenshot", label: "Take screenshot" },
      { value: "reboot", label: "Reboot device" },
      { value: "shell", label: "Open ADB shell" },
      { value: "back", label: "Back to menu" },
    ],
  });

  if (p.isCancel(action) || action === "back") return;

  switch (action) {
    case "launch":
      try {
        launchApp();
        p.log.success("App launched.");
      } catch (e: any) {
        p.log.error(e.message);
      }
      break;

    case "stop":
      try {
        forceStopApp();
        p.log.success("App stopped.");
      } catch (e: any) {
        p.log.error(e.message);
      }
      break;

    case "screenshot":
      await takeScreenshot();
      break;

    case "reboot":
      await rebootDevice();
      break;

    case "shell":
      await openShell();
      break;
  }
}
