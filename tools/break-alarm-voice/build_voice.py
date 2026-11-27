#!/usr/bin/env python3
"""Builds Assets/RealityLog/Resources/sx_break_alarm_voice.bytes: what the break alarm says.

One spoken phrase per pod number, "Pod N, start now.", for N = 1..99, and one with no number,
"Start now.", for a headset whose pod name carries none. BreakAlarmVoice.cs reads the file.

Needs piper-tts (pip install piper-tts), a piper voice and ffmpeg:
    python -m piper.download_voices en_US-ljspeech-high
    python tools/break-alarm-voice/build_voice.py en_US-ljspeech-high.onnx
The shipped file was built with en_US-ljspeech-high (LJ Speech, public domain). Each phrase is
trimmed of silence, high-passed and compressed so it carries across the room from the headset's
speakers, peak-normalised, and stored as 16 kHz mono 16-bit PCM.

File layout, little-endian: "SXBV", u32 version (1), u32 sample rate, u32 count, then per phrase
i32 pod number (0 = no number), u32 first sample, u32 sample count; then the samples (s16).
"""
import array
import io
import struct
import subprocess
import sys
import wave
from pathlib import Path

from piper import PiperVoice

RATE = 16000
PODS = range(1, 100)
FILTERS = (
    "silenceremove=start_periods=1:start_threshold=-45dB,areverse,"
    "silenceremove=start_periods=1:start_threshold=-45dB,areverse,"
    "highpass=f=180,acompressor=threshold=-26dB:ratio=8:attack=2:release=80:makeup=6"
)


def say(voice: PiperVoice, text: str) -> array.array:
    spoken = io.BytesIO()
    with wave.open(spoken, "wb") as out:
        voice.synthesize_wav(text, out)
    pcm = subprocess.run(
        ["ffmpeg", "-v", "error", "-i", "pipe:0", "-af", FILTERS, "-ac", "1", "-ar", str(RATE), "-f", "s16le", "pipe:1"],
        input=spoken.getvalue(), stdout=subprocess.PIPE, check=True,
    ).stdout
    samples = array.array("h")
    samples.frombytes(pcm)
    if sys.byteorder == "big":
        samples.byteswap()
    peak = max(1, max(abs(s) for s in samples))
    gain = 0.97 * 32767 / peak
    edge = RATE // 200  # 5 ms in and out, so a phrase never clicks
    count = len(samples)
    for i in range(count):
        ramp = min(1.0, min(i, count - 1 - i) / edge)
        samples[i] = int(max(-32767, min(32767, samples[i] * gain * ramp)))
    return samples


def main() -> int:
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    voice = PiperVoice.load(sys.argv[1])
    phrases = [(0, say(voice, "Start now."))]
    phrases += [(pod, say(voice, f"Pod {pod}, start now.")) for pod in PODS]

    header = struct.pack("<4sIII", b"SXBV", 1, RATE, len(phrases))
    table, body, at = b"", array.array("h"), 0
    for pod, samples in phrases:
        table += struct.pack("<iII", pod, at, len(samples))
        body.extend(samples)
        at += len(samples)
    if sys.byteorder == "big":
        body.byteswap()
    out = Path(__file__).resolve().parents[2] / "Assets/RealityLog/Resources/sx_break_alarm_voice.bytes"
    out.write_bytes(header + table + body.tobytes())
    longest = max(len(s) for _, s in phrases) / RATE
    print(f"{out}: {len(phrases)} phrases, {out.stat().st_size} bytes, longest {longest:.2f} s")
    return 0


if __name__ == "__main__":
    sys.exit(main())
