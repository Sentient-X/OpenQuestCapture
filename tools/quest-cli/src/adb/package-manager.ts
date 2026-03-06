import { adb, adbSync, adbShellSync, AdbError } from "./adb";
import { PACKAGE_NAME } from "../constants";

export function getInstalledVersion(): string | null {
  try {
    const output = adbShellSync(`dumpsys package ${PACKAGE_NAME} | grep versionName`);
    const match = output.match(/versionName=(\S+)/);
    return match ? match[1] : null;
  } catch {
    return null;
  }
}

export function isAppInstalled(): boolean {
  try {
    const output = adbShellSync(`pm list packages ${PACKAGE_NAME}`);
    return output.includes(PACKAGE_NAME);
  } catch {
    return false;
  }
}

export async function installApk(apkPath: string): Promise<string> {
  return adb(["install", "-r", "-g", apkPath]);
}

export async function uninstallApp(): Promise<string> {
  return adb(["uninstall", PACKAGE_NAME]);
}

export function launchApp(): string {
  return adbShellSync(
    `monkey -p ${PACKAGE_NAME} -c android.intent.category.LAUNCHER 1`
  );
}

export function forceStopApp(): string {
  return adbShellSync(`am force-stop ${PACKAGE_NAME}`);
}

export function isAppRunning(): boolean {
  try {
    const output = adbShellSync(`pidof ${PACKAGE_NAME}`);
    return output.trim().length > 0;
  } catch {
    return false;
  }
}
