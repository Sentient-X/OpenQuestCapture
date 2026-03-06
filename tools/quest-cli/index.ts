#!/usr/bin/env bun
import * as p from "@clack/prompts";
import pc from "picocolors";
import { ensureAdbAvailable, getDeviceSerial } from "./src/adb/adb";
import { mainMenu } from "./src/ui/menu";

// Handle --help
if (process.argv.includes("--help") || process.argv.includes("-h")) {
  console.log(`
${pc.bold("OpenQuestCapture CLI")}

Interactive tool for managing Quest recordings, device status, and ADB workflows.

${pc.dim("Usage:")}
  bun tools/quest-cli/index.ts

${pc.dim("Requirements:")}
  - ADB (Android platform-tools) in PATH
  - Quest connected via USB with developer mode enabled

${pc.dim("Commands:")}
  Device Dashboard    View battery, storage, device info
  Session Browser     List all sessions (device + local)
  Pull Sessions       Download recordings from Quest
  Delete Sessions     Remove sessions from device or local
  Live Monitor        Stream filtered logcat output
  APK Management      Install/uninstall/check app version
  Quick Actions       Launch, stop, reboot, screenshot, shell
`);
  process.exit(0);
}

// Main entry
p.intro(pc.bold("OpenQuestCapture CLI"));

// Preflight: check ADB
try {
  ensureAdbAvailable();
} catch {
  p.log.error("ADB not found in PATH. Install Android platform-tools first.");
  p.log.info(pc.dim("https://developer.android.com/tools/releases/platform-tools"));
  process.exit(1);
}

// Check device (non-fatal)
const serial = getDeviceSerial();
if (serial) {
  p.log.success(`Device connected: ${pc.dim(serial)}`);
} else {
  p.log.warn("No device detected. Some features will be unavailable.");
}

await mainMenu();
