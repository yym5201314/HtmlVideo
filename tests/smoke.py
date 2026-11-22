"""End-to-end checks against actual Chromium rendering and H.264/AAC decoding."""
import json
import base64
import math
import pathlib
import re
import struct
import subprocess
import sys
import wave

ROOT = pathlib.Path(__file__).resolve().parents[1]
EXE = ROOT / "dist" / "HtmlVideoLite.exe"
FFMPEG = ROOT / "tools" / "ffmpeg.exe"
RESULTS = ROOT / "build" / "smoke"


def convert(name, html="animation.html", success=True, *args):
    report = RESULTS / (name + ".json")
    output = RESULTS / (name + ".mp4")
    command = [
        str(EXE), "--input", str(ROOT / "tests" / html), "--output", str(output),
        "--ffmpeg", str(FFMPEG), "--width", "640", "--height", "360",
        "--fps", "10", "--report", str(report), "--overwrite",
    ]
    for i in range(0, len(args), 2):
        if args[i] in command:
            command[command.index(args[i]) + 1] = args[i + 1]
        else:
            command.extend(args[i:i + 2])
    result = subprocess.run(command, timeout=150)
    data = json.loads(report.read_text(encoding="utf-8"))
    assert data["success"] == success, data
    assert (result.returncode == 0) == success, result.returncode
    print(name, "PASS", data["error"] or data["messages"][-1])
    return output, data


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    RESULTS.mkdir(parents=True, exist_ok=True)
    output, data = convert("动画 # 中文 %")
    decoded = subprocess.run([
        str(FFMPEG), "-v", "error", "-i", str(output),
        "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1",
    ], capture_output=True, timeout=30, check=True).stdout
    frame_size = 640 * 360 * 3
    assert len(decoded) == frame_size * 12, len(decoded)
    frames = [decoded[i:i + frame_size] for i in range(0, len(decoded), frame_size)]
    assert len(set(frames)) >= 10, "Animation did not move"
    # The red box starts near the left edge and must travel right.
    def red_center(frame):
        positions = [x for x in range(640) if frame[(140*640+x)*3] > 140
                     and frame[(140*640+x)*3+1] < 90]
        assert positions, "Red animation missing from rendered stage"
        return sum(positions) / len(positions)
    assert red_center(frames[0]) < 90
    assert red_center(frames[-1]) > 440
    def green_center(frame):
        positions = [x for x in range(640) if frame[(240*640+x)*3+1] > 95
                     and frame[(240*640+x)*3] < 70]
        assert positions, "CSS animation missing"
        return sum(positions) / len(positions)
    assert green_center(frames[0]) < 100
    assert green_center(frames[8]) > 350
    assert not any("页面脚本警告" in m for m in data["messages"]), data
    _, precise = convert("precise", "animation.html", True, "--precise", "--mute")
    assert any("PNG + medium" in m for m in precise["messages"]), precise
    missing_html = RESULTS / "missing.html"
    missing_html.write_text(
        '<!doctype html><body data-duration="0.2"><img src="missing-image.jpg">'
        '<audio src="missing-audio.wav"></audio></body>', encoding="utf-8")
    _, missing = convert("missing-resources", str(missing_html))
    assert any("素材未加载：" in m and "missing-image.jpg" in m for m in missing["messages"]), missing
    assert any("音轨文件不存在" in m and "missing-audio.wav" in m for m in missing["messages"]), missing
    tone = RESULTS / "音频.wav"
    with wave.open(str(tone), "wb") as wav:
        wav.setparams((1, 2, 22050, 0, "NONE", "not compressed"))
        wav.writeframes(b"".join(struct.pack("<h", int(6000 * math.sin(i * 2 * math.pi * 440 / 22050)))
                                 for i in range(11025)))
    audio_output, _ = convert("audio", "animation.html", True, "--audio", str(tone))
    info = subprocess.run([str(FFMPEG), "-i", str(audio_output)], capture_output=True).stderr.decode("utf-8", "replace")
    assert re.search(r"Audio: aac", info), info
    pcm = subprocess.run([str(FFMPEG), "-v", "error", "-i", str(audio_output), "-vn", "-f", "s16le", "pipe:1"],
                         capture_output=True, timeout=30, check=True).stdout
    assert len(pcm) > 40000 and any(pcm[-4096:]), "Looped audio is missing"
    embedded = RESULTS / "embedded.html"
    embedded.write_text(
        '<!doctype html><meta charset="utf-8"><body data-duration="0.4">'
        '<h1>Embedded audio</h1><audio src="data:audio/wav;base64,'
        + base64.b64encode(tone.read_bytes()).decode("ascii") + '"></audio></body>',
        encoding="utf-8")
    embedded_output, _ = convert("embedded-audio", str(embedded))
    info = subprocess.run([str(FFMPEG), "-i", str(embedded_output)], capture_output=True).stderr
    assert b"Audio: aac" in info
    media = RESULTS / "media.html"
    media.write_text(
        '<!doctype html><meta charset="utf-8"><body style="margin:0">'
        '<video id="stage" width="640" height="360" autoplay muted src="'
        + audio_output.as_uri() + '"></video></body>', encoding="utf-8")
    media_output, _ = convert("media-seek", str(media), True, "--duration", "0.6")
    video = subprocess.run([
        str(FFMPEG), "-v", "error", "-i", str(media_output),
        "-an", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1",
    ], capture_output=True, timeout=30, check=True).stdout
    assert len(video) == frame_size * 6
    assert red_center(video[:frame_size]) < 100
    assert red_center(video[-frame_size:]) > 200, "Video element did not seek with virtual time"
    convert("unknown-duration", "static.html", False)
    _, static = convert("static", "static.html", True, "--duration", "0.4")
    assert any("完全静止" in m for m in static["messages"]), static
    convert("odd-width", "animation.html", False, "--width", "641")
    convert("bad-selector", "animation.html", False, "--start", "#does-not-exist")
    invalid_audio = RESULTS / "invalid.wav"
    invalid_audio.write_bytes(b"not an audio file")
    original_output = RESULTS / "bad-audio.mp4"
    original_output.write_bytes(b"original-output-must-survive")
    convert("bad-audio", "animation.html", False, "--audio", str(invalid_audio))
    assert original_output.read_bytes() == b"original-output-must-survive"
    print("All integration checks passed.")


if __name__ == "__main__":
    main()
