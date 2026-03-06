import pc from "picocolors";
import { getQuickDeviceStatus } from "../adb/device";
import { rawSelect, isRawCancel } from "./raw-select";
import { dashboardCommand } from "../commands/dashboard";
import { sessionBrowserCommand } from "../commands/session-browser";
import { pullSessionsCommand } from "../commands/pull-sessions";
import { deleteSessionsCommand } from "../commands/delete-sessions";
import { liveMonitorCommand } from "../commands/live-monitor";
import { apkManagementCommand } from "../commands/apk-management";
import { quickActionsCommand } from "../commands/quick-actions";

function buildStatusLine(): string {
  const status = getQuickDeviceStatus();
  if (!status) return pc.yellow("No device connected");
  return `${pc.bold(status.model)} (${pc.dim(status.serial)}) | Battery: ${status.battery}% | Storage: ${status.storageFreePercent}% free`;
}

type MenuAction =
  | "dashboard"
  | "sessions"
  | "pull"
  | "delete"
  | "monitor"
  | "apk"
  | "quick"
  | "exit";

export async function mainMenu(): Promise<void> {
  while (true) {
    console.log();
    console.log(pc.dim("─".repeat(60)));
    console.log(`  ${buildStatusLine()}`);
    console.log(pc.dim("─".repeat(60)));

    const action = await rawSelect<MenuAction>("What would you like to do?", [
      { value: "dashboard", label: "Device Dashboard", hint: "battery, storage, info" },
      { value: "sessions", label: "Session Browser", hint: "list all sessions" },
      { value: "pull", label: "Pull Sessions", hint: "download from Quest" },
      { value: "delete", label: "Delete Sessions", hint: "remove from device/local" },
      { value: "monitor", label: "Live Monitor", hint: "filtered logcat" },
      { value: "apk", label: "APK Management", hint: "install, version" },
      { value: "quick", label: "Quick Actions", hint: "reboot, screenshot, launch" },
      { value: "exit", label: "Exit" },
    ]);

    if (isRawCancel(action) || action === "exit") {
      console.log(pc.dim("Goodbye!"));
      return;
    }

    switch (action) {
      case "dashboard":
        await dashboardCommand();
        break;
      case "sessions": {
        const result = await sessionBrowserCommand();
        if (result === "pull") await pullSessionsCommand();
        break;
      }
      case "pull":
        await pullSessionsCommand();
        break;
      case "delete":
        await deleteSessionsCommand();
        break;
      case "monitor":
        await liveMonitorCommand();
        break;
      case "apk":
        await apkManagementCommand();
        break;
      case "quick":
        await quickActionsCommand();
        break;
    }
  }
}
