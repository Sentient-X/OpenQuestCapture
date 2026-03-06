import { adbShellSync, requireDevice } from "./adb";
import { PACKAGE_NAME, DEVICE_FILES_DIR } from "../constants";
import type { DeviceInfo } from "../types";

function prop(key: string): string {
  return adbShellSync(`getprop ${key}`).trim();
}

function getBatteryLevel(): number {
  const output = adbShellSync("dumpsys battery");
  const match = output.match(/level:\s*(\d+)/);
  return match ? parseInt(match[1], 10) : -1;
}

function isBatteryCharging(): boolean {
  const output = adbShellSync("dumpsys battery");
  // status: 2 = charging, 5 = full
  const match = output.match(/status:\s*(\d+)/);
  if (!match) return false;
  const status = parseInt(match[1], 10);
  return status === 2 || status === 5;
}

function getStorageInfo(): { total: number; free: number } {
  const output = adbShellSync("df /sdcard");
  // Parse df output: Filesystem  1K-blocks  Used  Available  Use%  Mounted on
  const lines = output.split("\n").filter((l) => l.includes("/"));
  if (lines.length === 0) return { total: 0, free: 0 };
  const parts = lines[0].trim().split(/\s+/);
  // parts: filesystem, total, used, available, use%, mount
  const total = parseInt(parts[1], 10) * 1024; // Convert from 1K-blocks to bytes
  const free = parseInt(parts[3], 10) * 1024;
  return { total: isNaN(total) ? 0 : total, free: isNaN(free) ? 0 : free };
}

function getAppVersion(): string | null {
  try {
    const output = adbShellSync(`dumpsys package ${PACKAGE_NAME} | grep versionName`);
    const match = output.match(/versionName=(\S+)/);
    return match ? match[1] : null;
  } catch {
    return null;
  }
}

function getDeviceModel(): string {
  try {
    return prop("ro.product.model");
  } catch {
    return "Unknown";
  }
}

function getAndroidVersion(): string {
  try {
    return prop("ro.build.version.release");
  } catch {
    return "Unknown";
  }
}

export function getDeviceSessionCount(): number {
  try {
    const output = adbShellSync(`ls -1 ${DEVICE_FILES_DIR}`);
    const lines = output.split("\n").filter((l) => /^\d{8}_\d{6}$/.test(l.trim()));
    return lines.length;
  } catch {
    return 0;
  }
}

export function getDeviceInfo(): DeviceInfo {
  const serial = requireDevice();
  const storage = getStorageInfo();

  return {
    serial,
    model: getDeviceModel(),
    androidVersion: getAndroidVersion(),
    batteryLevel: getBatteryLevel(),
    batteryCharging: isBatteryCharging(),
    storageTotal: storage.total,
    storageFree: storage.free,
    appVersion: getAppVersion(),
  };
}

/** Get a quick summary for the menu header. Returns null if no device. */
export function getQuickDeviceStatus(): {
  model: string;
  serial: string;
  battery: number;
  storageFreePercent: number;
} | null {
  try {
    const serial = requireDevice();
    const model = getDeviceModel();
    const battery = getBatteryLevel();
    const storage = getStorageInfo();
    const storageFreePercent =
      storage.total > 0 ? Math.round((storage.free / storage.total) * 100) : 0;
    return { model, serial: serial.slice(0, 8), battery, storageFreePercent };
  } catch {
    return null;
  }
}
