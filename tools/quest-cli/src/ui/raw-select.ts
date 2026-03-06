import pc from "picocolors";

export interface SelectOption<T> {
  value: T;
  label: string;
  hint?: string;
}

/**
 * Simple select menu using raw stdin. Avoids readline entirely
 * to work around Bun 1.0.0 readline leak that causes hangs.
 */
export async function rawSelect<T>(
  message: string,
  options: SelectOption<T>[]
): Promise<T | symbol> {
  const CANCEL = Symbol("cancel");
  let cursor = 0;

  const render = () => {
    // Move cursor up to overwrite previous render (if any)
    process.stdout.write(`\x1b[?25l`); // hide cursor

    let out = `${pc.gray("│")}\n`;
    out += `${pc.green("◆")}  ${message}\n`;

    for (let i = 0; i < options.length; i++) {
      const opt = options[i];
      const prefix = i === cursor ? pc.green("●") : pc.dim("○");
      const label = i === cursor ? opt.label : pc.dim(opt.label);
      const hint = opt.hint && i === cursor ? pc.dim(` (${opt.hint})`) : "";
      out += `${pc.gray("│")}  ${prefix} ${label}${hint}\n`;
    }
    out += `${pc.gray("└")}\n`;
    return out;
  };

  // Initial render
  let output = render();
  process.stdout.write(output);
  const lineCount = output.split("\n").length - 1;

  return new Promise<T | symbol>((resolve) => {
    const { stdin } = process;
    const wasRaw = stdin.isRaw;
    stdin.setRawMode(true);
    stdin.resume();

    const onData = (data: Buffer) => {
      const key = data.toString();

      // Ctrl+C or Escape
      if (key === "\x03" || key === "\x1b") {
        cleanup();
        resolve(CANCEL);
        return;
      }

      // Enter
      if (key === "\r" || key === "\n") {
        cleanup();
        // Print final state
        process.stdout.write(
          `${pc.gray("│")}\n${pc.green("◆")}  ${message}\n${pc.gray("│")}  ${options[cursor].label}\n${pc.gray("│")}\n`
        );
        resolve(options[cursor].value);
        return;
      }

      // Arrow up or k
      if (key === "\x1b[A" || key === "k") {
        cursor = (cursor - 1 + options.length) % options.length;
      }
      // Arrow down or j
      else if (key === "\x1b[B" || key === "j") {
        cursor = (cursor + 1) % options.length;
      }
      // Home
      else if (key === "\x1b[H") {
        cursor = 0;
      }
      // End
      else if (key === "\x1b[F") {
        cursor = options.length - 1;
      } else {
        return; // Unknown key, don't re-render
      }

      // Clear previous output and re-render
      process.stdout.write(`\x1b[${lineCount}A\x1b[0J`);
      output = render();
      process.stdout.write(output);
    };

    const cleanup = () => {
      stdin.removeListener("data", onData);
      stdin.setRawMode(wasRaw ?? false);
      stdin.pause();
      // Clear the interactive output
      process.stdout.write(`\x1b[${lineCount}A\x1b[0J`);
      process.stdout.write(`\x1b[?25h`); // show cursor
    };

    stdin.on("data", onData);
  });
}

/** Check if a value is the cancel symbol. */
export function isRawCancel(value: unknown): value is symbol {
  return typeof value === "symbol";
}
