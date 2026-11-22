"""Compile the in-process layout/cancellation tests with the same Framework compiler."""
import pathlib
import subprocess
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
ROOT = pathlib.Path(__file__).resolve().parents[1]
exe = ROOT / "build" / "Harness.exe"
command = [
    r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe", "/nologo", "/target:exe",
    "/main:HtmlVideoLite.Harness", "/codepage:65001", "/utf8output",
    "/win32manifest:" + str(ROOT / "app.manifest"), "/out:" + str(exe),
]
command += ["/r:" + name + ".dll" for name in (
    "System", "System.Core", "System.Drawing", "System.Windows.Forms", "System.Web.Extensions",
)]
command += ["/resource:" + str(p) + "," + p.name for p in (ROOT / "src").glob("*.js")]
command += [str(p) for p in (ROOT / "src").glob("*.cs")]
command += [str(ROOT / "tests" / "Harness.cs")]
subprocess.run(command, check=True)
launcher = list(command)
launcher[launcher.index("/main:HtmlVideoLite.Harness")] = "/main:HtmlVideoLite.BrowserLauncher"
launcher[launcher.index("/out:" + str(exe))] = "/out:" + str(ROOT / "build" / "BrowserLauncher.exe")
launcher[-1] = str(ROOT / "tests" / "BrowserLauncher.cs")
subprocess.run(launcher, check=True)
subprocess.run([str(exe), str(ROOT)], check=True, timeout=90)
