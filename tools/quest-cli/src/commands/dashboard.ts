import * as p from "@clack/prompts";
import pc from "picocolors";
import { getDeviceInfo, getDeviceSessionCount } from "../adb/device";
import {
  humanBytes,
  storageBar,
  batteryIndicator,
  divider,
} from "../ui/formatters";

export async function dashboardCommand(): Promise<void> {
  const s = p.spinner();
  s.start("Querying device...");

  try {
    const info = getDeviceInfo();
    const sessionCount = getDeviceSessionCount();
    s.stop("Device info loaded.");

    const usedPercent =
      info.storageTotal > 0
        ? Math.round(
            ((info.storageTotal - info.storageFree) / info.storageTotal) * 100
          )
        : 0;

    console.log();
    console.log(divider());
    console.log(pc.bold("  Device Dashboard"));
    console.log(divider());
    console.log();
    console.log(`  Model:           ${pc.bold(info.model)}`);
    console.log(`  Serial:          ${pc.dim(info.serial)}`);
    console.log(`  Android:         ${info.androidVersion}`);
    console.log();
    console.log(`  Battery:         ${batteryIndicator(info.batteryLevel, info.batteryCharging)}`);
    console.log(`  Storage:         ${storageBar(usedPercent)}`);
    console.log(`                   ${humanBytes(info.storageFree)} free of ${humanBytes(info.storageTotal)}`);
    console.log();
    console.log(`  App Version:     ${info.appVersion ?? pc.dim("not installed")}`);
    console.log(`  Sessions:        ${sessionCount} on device`);
    console.log();
    console.log(divider());
    console.log();
  } catch (e: any) {
    s.stop("Failed to query device.");
    p.log.error(e.message);
  }
}
