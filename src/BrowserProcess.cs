using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HtmlVideoLite
{
    internal sealed class BrowserProcess : IDisposable
    {
        readonly ProcessJob job = new ProcessJob();
        readonly StringBuilder output = new StringBuilder();
        Process process;
        internal string Endpoint { get; private set; }

        internal static async Task<BrowserProcess> Start(Options options, string work, Action<Update> progress, CancellationToken token)
        {
            var diagnostics = new StringBuilder();
            var failures = new List<string>();
            diagnostics.AppendLine(DateTime.Now.ToString("s"));
            diagnostics.AppendLine("Browser: " + options.Browser);
            for (int mode = 0; mode < 2; mode++)
            {
                var browser = new BrowserProcess();
                string name = mode == 0 ? "标准渲染" : "软件渲染";
                try
                {
                    progress(new Update(0, "启动浏览器（" + name + "）…"));
                    await browser.Launch(options, Path.Combine(work, "browser-" + mode), mode == 1, token).ConfigureAwait(false);
                    diagnostics.AppendLine(name + ": connected " + browser.Endpoint);
                    SaveDiagnostics(diagnostics.ToString());
                    return browser;
                }
                catch (OperationCanceledException) { browser.Dispose(); throw; }
                catch (Exception e)
                {
                    browser.Dispose();
                    if (!(e is IOException) && !(e is TimeoutException) &&
                        !(e is System.ComponentModel.Win32Exception) && !(e is WebSocketException)) throw;
                    diagnostics.AppendLine(name + ": " + e.Message);
                    string detail = browser.Output().Trim();
                    diagnostics.AppendLine(detail);
                    failures.Add(name + "：" + e.Message + (detail.Length == 0 ? "" : "\r\n" +
                        detail.Substring(Math.Max(0, detail.Length - 600))));
                    if (mode == 0)
                        progress(new Update(0, "标准启动失败，正在重试软件渲染…"));
                }
            }
            string log = SaveDiagnostics(diagnostics.ToString());
            throw new IOException("浏览器启动失败，已尝试标准和软件渲染。\r\n" +
                string.Join("\r\n", failures) + "\r\n启动日志：" + log);
        }

        async Task Launch(Options options, string profile, bool software, CancellationToken token)
        {
            var args = new List<string> {
                "--headless=new", "--remote-debugging-address=127.0.0.1", "--remote-debugging-port=0",
                "--user-data-dir=" + profile, "--no-first-run", "--no-default-browser-check",
                "--autoplay-policy=no-user-gesture-required", "--mute-audio", "--hide-scrollbars",
                "--force-device-scale-factor=1", "--force-color-profile=srgb",
                "--allow-file-access-from-files", "--disable-background-timer-throttling",
                "--disable-renderer-backgrounding", "--window-size=" + options.Width + "," + options.Height
            };
            if (string.Equals(Path.GetFileName(options.Browser), "msedge.exe", StringComparison.OrdinalIgnoreCase))
                args.Add("--edge-skip-compat-layer-relaunch");
            if (software) args.Add("--disable-gpu");
            args.Add("about:blank");
            var info = Engine.StartInfo(options.Browser, args);
            info.RedirectStandardError = info.RedirectStandardOutput = true;
            info.StandardErrorEncoding = info.StandardOutputEncoding = Encoding.UTF8;
            process = new Process { StartInfo = info };
            process.ErrorDataReceived += Capture;
            process.OutputDataReceived += Capture;
            process.Start();
            job.Add(process);
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            string active = Path.Combine(profile, "DevToolsActivePort");
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 20000)
            {
                token.ThrowIfCancellationRequested();
                // A Chromium launcher can exit after handing over to its child.
                // The private profile's live CDP endpoint is the readiness signal.
                string[] portInfo = null;
                try
                {
                    if (File.Exists(active)) portInfo = File.ReadAllLines(active);
                }
                catch (IOException) { }
                int port;
                if (portInfo != null && portInfo.Length >= 2 &&
                    int.TryParse(portInfo[0], out port) && port > 0 && port <= 65535 &&
                    portInfo[1].StartsWith("/devtools/browser/", StringComparison.Ordinal))
                {
                    Endpoint = "ws://127.0.0.1:" + port + portInfo[1];
                    using (var probe = new Cdp(token))
                    {
                        await probe.Connect(Endpoint).ConfigureAwait(false);
                        await probe.Call("Browser.getVersion", new { }).ConfigureAwait(false);
                    }
                    return;
                }
                if (process.HasExited && process.ExitCode != 0 && watch.ElapsedMilliseconds >= 1000)
                    throw new IOException("浏览器进程退出，退出码 " + process.ExitCode +
                        "（0x" + process.ExitCode.ToString("X8") + "）。");
                await Task.Delay(100, token).ConfigureAwait(false);
            }
            throw new TimeoutException("20 秒内未发现可用的浏览器调试端口。" +
                (process.HasExited ? "启动进程退出码：" + process.ExitCode : "启动进程仍在运行。"));
        }

        void Capture(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            lock (output)
            {
                output.AppendLine(e.Data);
                if (output.Length > 12000) output.Remove(0, output.Length - 12000);
            }
        }

        string Output() { lock (output) return output.ToString(); }

        static string SaveDiagnostics(string text)
        {
            try
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HtmlVideoLite", "logs");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "last-browser-startup.log");
                File.WriteAllText(path, text, new UTF8Encoding(false));
                return path;
            }
            catch (IOException e) { return "无法写入日志：" + e.Message; }
            catch (UnauthorizedAccessException e) { return "无法写入日志：" + e.Message; }
        }

        public void Dispose()
        {
            job.Dispose();
            if (process != null)
            {
                try { process.WaitForExit(5000); }
                catch (InvalidOperationException) { }
                process.Dispose();
                process = null;
            }
        }
    }
}
