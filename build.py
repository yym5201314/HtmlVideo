"""Build with the Windows .NET Framework compiler. No pip or npm dependencies."""
import argparse
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parent


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    parser = argparse.ArgumentParser()
    parser.add_argument("--version")
    args = parser.parse_args()
    version = args.version or (ROOT / "VERSION").read_text(encoding="utf-8").strip()
    m = re.fullmatch(r"(\d+)\.(\d+)\.(\d+)", version)
    if not m:
        raise SystemExit("Invalid version: " + version)
    if int(m.group(3)) > 9:
        raise SystemExit("Patch version must be 0-9 (bump the minor instead): " + version)
    if any(int(part) > 65534 for part in version.split(".")):
        raise SystemExit("Assembly version components must be <= 65534")
    compiler = pathlib.Path(r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe")
    if not compiler.exists():
        compiler = pathlib.Path(r"C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe")
    (ROOT / "build").mkdir(exist_ok=True)
    (ROOT / "dist").mkdir(exist_ok=True)
    info = ROOT / "build" / "AssemblyInfo.cs"
    info.write_text(
        '[assembly:System.Reflection.AssemblyTitle("HTML Video Lite")]\n'
        '[assembly:System.Reflection.AssemblyProduct("HTML Video Lite")]\n'
        f'[assembly:System.Reflection.AssemblyVersion("{version}.0")]\n'
        f'[assembly:System.Reflection.AssemblyFileVersion("{version}.0")]\n',
        encoding="utf-8",
    )
    exe = ROOT / "dist" / "HtmlVideoLite.exe"
    cmd = [
        str(compiler), "/nologo", "/target:winexe", "/platform:anycpu", "/optimize+",
        "/codepage:65001", "/utf8output", "/win32manifest:" + str(ROOT / "app.manifest"),
        "/out:" + str(exe),
    ]
    cmd += ["/r:" + name + ".dll" for name in (
        "System", "System.Core", "System.Drawing", "System.Windows.Forms",
        "System.Web.Extensions",
    )]
    cmd += ["/resource:" + str(p) + "," + p.name for p in sorted((ROOT / "src").glob("*.js"))]
    sources = []
    for source in sorted((ROOT / "src").glob("*.cs")):
        if source.name == "Program.cs":
            generated = ROOT / "build" / "Program.cs"
            text = source.read_text(encoding="utf-8")
            text, count = re.subn(r'const string APP_VERSION = "[^"]+";',
                                 f'const string APP_VERSION = "{version}";', text)
            if count != 1:
                raise SystemExit("Expected one APP_VERSION declaration")
            generated.write_text(text, encoding="utf-8")
            sources.append(str(generated))
        else:
            sources.append(str(source))
    cmd += [str(info)] + sources
    subprocess.run(cmd, check=True)
    size = exe.stat().st_size
    if size > 300 * 1024:
        raise SystemExit("EXE exceeds 300 KiB budget")
    print(f"Built {exe} ({size:,} bytes, {size / 1024:.1f} KiB)")


if __name__ == "__main__":
    main()
