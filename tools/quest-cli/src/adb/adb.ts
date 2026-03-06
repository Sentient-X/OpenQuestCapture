import { spawnSync, spawn, type Subprocess } from "bun";

export class AdbError extends Error {
  constructor(
    message: string,
    public readonly exitCode: number | null = null,
    public readonly stderr: string = ""
  ) {
    super(message);
    this.name = "AdbError";
  }
}

/** Run an ADB command synchronously. Returns stdout. */
export function adbSync(args: string[]): string {
  const result = spawnSync(["adb", ...args]);
  const stdout = result.stdout.toString().trim();
  const stderr = result.stderr.toString().trim();

  if (result.exitCode !== 0) {
    throw new AdbError(
      `adb ${args.join(" ")} failed (exit ${result.exitCode}): ${stderr || stdout}`,
      result.exitCode,
      stderr
    );
  }
  return stdout;
}

/** Run an ADB command asynchronously. Returns stdout. */
export async function adb(args: string[]): Promise<string> {
  const proc = spawn(["adb", ...args], {
    stdout: "pipe",
    stderr: "pipe",
  });

  const stdout = await new Response(proc.stdout).text();
  const stderr = await new Response(proc.stderr).text();
  const exitCode = await proc.exited;

  if (exitCode !== 0) {
    throw new AdbError(
      `adb ${args.join(" ")} failed (exit ${exitCode}): ${stderr.trim() || stdout.trim()}`,
      exitCode,
      stderr.trim()
    );
  }
  return stdout.trim();
}

/** Shorthand for `adb shell "cmd"` (sync). */
export function adbShellSync(cmd: string): string {
  return adbSync(["shell", cmd]);
}

/** Shorthand for `adb shell "cmd"` (async). */
export async function adbShell(cmd: string): Promise<string> {
  return adb(["shell", cmd]);
}

/** Spawn an ADB subprocess for streaming (logcat, pull). Returns the Subprocess. */
export function adbSpawn(
  args: string[],
  options?: { onStdout?: (line: string) => void; onStderr?: (line: string) => void }
): Subprocess {
  const proc = spawn(["adb", ...args], {
    stdout: "pipe",
    stderr: "pipe",
  });

  if (options?.onStdout && proc.stdout) {
    const reader = proc.stdout.getReader();
    const decoder = new TextDecoder();
    let buffer = "";

    (async () => {
      try {
        while (true) {
          const { done, value } = await reader.read();
          if (done) break;
          buffer += decoder.decode(value, { stream: true });
          const lines = buffer.split("\n");
          buffer = lines.pop() || "";
          for (const line of lines) {
            options.onStdout!(line);
          }
        }
        if (buffer) options.onStdout!(buffer);
      } catch {
        // Stream closed
      }
    })();
  }

  if (options?.onStderr && proc.stderr) {
    const reader = proc.stderr.getReader();
    const decoder = new TextDecoder();
    let buffer = "";

    (async () => {
      try {
        while (true) {
          const { done, value } = await reader.read();
          if (done) break;
          buffer += decoder.decode(value, { stream: true });
          const lines = buffer.split("\n");
          buffer = lines.pop() || "";
          for (const line of lines) {
            options.onStderr!(line);
          }
        }
        if (buffer) options.onStderr!(buffer);
      } catch {
        // Stream closed
      }
    })();
  }

  return proc;
}

/** Check if ADB is available in PATH. */
export function ensureAdbAvailable(): void {
  const result = spawnSync(["which", "adb"]);
  if (result.exitCode !== 0) {
    throw new AdbError("adb not found in PATH. Install Android platform-tools.");
  }
}

/** Get the serial of a connected device, or null if none. */
export function getDeviceSerial(): string | null {
  try {
    const output = adbSync(["devices"]);
    const lines = output.split("\n").slice(1);
    for (const line of lines) {
      const parts = line.trim().split(/\s+/);
      if (parts.length >= 2 && parts[1] === "device") {
        return parts[0];
      }
    }
    return null;
  } catch {
    return null;
  }
}

/** Get the serial of a connected device or throw. */
export function requireDevice(): string {
  const output = adbSync(["devices"]);
  const lines = output.split("\n").slice(1);

  let unauthorized = false;
  for (const line of lines) {
    const parts = line.trim().split(/\s+/);
    if (parts.length < 2) continue;
    if (parts[1] === "device") return parts[0];
    if (parts[1] === "unauthorized") unauthorized = true;
  }

  if (unauthorized) {
    throw new AdbError(
      "Quest is connected but unauthorized. Put on the headset and allow USB debugging."
    );
  }
  throw new AdbError("No ADB device detected. Connect your Quest via USB.");
}
