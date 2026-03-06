import * as p from "@clack/prompts";
import pc from "picocolors";
import { readdirSync } from "fs";
import { join, resolve } from "path";
import {
  getInstalledVersion,
  isAppInstalled,
  installApk,
  uninstallApp,
} from "../adb/package-manager";
import { LOCAL_BUILDS_DIR, PACKAGE_NAME } from "../constants";

function findApkFiles(): string[] {
  const buildsDir = resolve(
    import.meta.dir,
    "..",
    "..",
    "..",
    "..",
    LOCAL_BUILDS_DIR
  );
  try {
    return readdirSync(buildsDir)
      .filter((f) => f.endsWith(".apk"))
      .map((f) => join(buildsDir, f))
      .sort();
  } catch {
    return [];
  }
}

export async function apkManagementCommand(): Promise<void> {
  const installed = isAppInstalled();
  const version = installed ? getInstalledVersion() : null;

  p.log.info(
    installed
      ? `${PACKAGE_NAME} v${version ?? "unknown"} installed`
      : `${PACKAGE_NAME} is ${pc.yellow("not installed")}`
  );

  const action = await p.select({
    message: "What would you like to do?",
    options: [
      { value: "install", label: "Install APK from Builds/" },
      ...(installed
        ? [{ value: "uninstall" as const, label: "Uninstall app" }]
        : []),
      { value: "check", label: "Check version" },
      { value: "back", label: "Back to menu" },
    ],
  });

  if (p.isCancel(action) || action === "back") return;

  if (action === "check") {
    const ver = getInstalledVersion();
    p.log.info(ver ? `Installed version: ${ver}` : "App not installed.");
    return;
  }

  if (action === "uninstall") {
    const confirmed = await p.confirm({
      message: `Uninstall ${PACKAGE_NAME}?`,
      initialValue: false,
    });
    if (p.isCancel(confirmed) || !confirmed) return;

    const s = p.spinner();
    s.start("Uninstalling...");
    try {
      await uninstallApp();
      s.stop("Uninstalled successfully.");
    } catch (e: any) {
      s.stop("Uninstall failed.");
      p.log.error(e.message);
    }
    return;
  }

  if (action === "install") {
    const apks = findApkFiles();
    if (apks.length === 0) {
      p.log.warn(`No APK files found in ${LOCAL_BUILDS_DIR}/`);
      return;
    }

    const apkPath = await p.select({
      message: "Select APK to install:",
      options: apks.map((path) => ({
        value: path,
        label: path.split("/").pop()!,
      })),
    });

    if (p.isCancel(apkPath)) return;

    const s = p.spinner();
    s.start("Installing APK...");
    try {
      await installApk(apkPath as string);
      s.stop("APK installed successfully.");
      const newVer = getInstalledVersion();
      if (newVer) p.log.info(`Version: ${newVer}`);
    } catch (e: any) {
      s.stop("Install failed.");
      p.log.error(e.message);
    }
  }
}
