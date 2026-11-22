using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HtmlVideoLite
{
    internal sealed class Options
    {
        internal string Input = "", Output = "", Audio = "", Browser = "", Ffmpeg = "", Stage = "", Start = "";
        internal int Width = 1920, Height = 1080, Fps = 30, Crf = 18;
        internal double Duration;
        internal bool Fit = true, Hide = true, Fast = true, Mute, Overwrite;
    }

    internal sealed class Update
    {
        internal int Percent;
        internal string Message;
        internal Update(int percent, string message) { Percent = percent; Message = message; }
    }

    internal static class Engine
    {
        internal static string Browser()
        {
            string[] candidates = {
                Environment.GetEnvironmentVariable("H2V_BROWSER"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe")
            };
            return candidates.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p)) ?? "";
        }

        internal static string Ffmpeg()
        {
            string root = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new List<string> { Environment.GetEnvironmentVariable("H2V_FFMPEG"),
                Path.Combine(root, "ffmpeg.exe"), Path.Combine(root, "tools", "ffmpeg.exe"),
                Path.GetFullPath(Path.Combine(root, "..", "tools", "ffmpeg.exe")) };
            candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')
                .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.Combine(p.Trim('"'), "ffmpeg.exe")));
            return candidates.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p)) ?? "";
        }

        static string Resource(string name)
        {
            using (var reader = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream(name), Encoding.UTF8))
                return reader.ReadToEnd();
        }

        internal static string Quote(string value)
        {
            // Windows CommandLineToArgvW quoting, including trailing backslashes.
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') result.Append('\\', slashes * 2 + 1).Append('"');
                else result.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }

        internal static ProcessStartInfo StartInfo(string file, IEnumerable<string> args)
        {
            return new ProcessStartInfo(file, string.Join(" ", args.Select(Quote))) {
                UseShellExecute = false, CreateNoWindow = true
            };
        }

        internal static void Validate(Options o)
        {
            o.Input = Path.GetFullPath(o.Input);
            if (!File.Exists(o.Input)) throw new ArgumentException("找不到 HTML 文件：" + o.Input);
            if (!new[] { ".html", ".htm" }.Contains(Path.GetExtension(o.Input).ToLowerInvariant()))
                throw new ArgumentException("请选择 .html 或 .htm 文件。");
            o.Output = Path.GetFullPath(string.IsNullOrWhiteSpace(o.Output) ? Path.ChangeExtension(o.Input, ".mp4") : o.Output);
            if (!string.Equals(Path.GetExtension(o.Output), ".mp4", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("输出文件扩展名必须为 .mp4。");
            if (string.Equals(o.Input, o.Output, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(o.Audio) && string.Equals(Path.GetFullPath(o.Audio), o.Output, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("输出文件不能覆盖输入文件。");
            if (File.Exists(o.Output) && !o.Overwrite) throw new IOException("输出文件已存在，请另选文件名或确认覆盖。");
            if (o.Width < 16 || o.Width > 7680 || o.Height < 16 || o.Height > 7680 || o.Width % 2 != 0 || o.Height % 2 != 0)
                throw new ArgumentException("宽高必须为 16–7680 范围内的偶数。");
            if (o.Fps < 1 || o.Fps > 120 || o.Crf < 0 || o.Crf > 51)
                throw new ArgumentException("帧率应为 1–120，CRF 应为 0–51。");
            if (double.IsNaN(o.Duration) || double.IsInfinity(o.Duration) || o.Duration < 0 || o.Duration > 3600 ||
                (o.Duration > 0 && o.Duration < 0.2))
                throw new ArgumentException("时长应为 0（自动）或 0.2–3600 秒。");
            if (!string.IsNullOrWhiteSpace(o.Audio) && !File.Exists(o.Audio))
                throw new ArgumentException("找不到音频文件：" + o.Audio);
            if (string.IsNullOrWhiteSpace(o.Browser)) o.Browser = Browser();
            if (string.IsNullOrWhiteSpace(o.Ffmpeg)) o.Ffmpeg = Ffmpeg();
            if (!File.Exists(o.Browser)) throw new IOException("未找到 Edge/Chrome，请指定浏览器路径。");
            if (!File.Exists(o.Ffmpeg)) throw new IOException("未找到 FFmpeg，请选择 ffmpeg.exe，或将它放到程序旁的 tools 文件夹。");
        }

        static async Task Load(Cdp page, Options o, CancellationToken token)
        {
            var navigation = await page.Call("Page.navigate", new { url = new Uri(o.Input).AbsoluteUri }).ConfigureAwait(false);
            if (navigation.ContainsKey("errorText")) throw new IOException("HTML 加载失败：" + navigation["errorText"]);
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 45000)
            {
                token.ThrowIfCancellationRequested();
                object state = await page.Evaluate("document.readyState").ConfigureAwait(false);
                if (Convert.ToString(state) == "complete") return;
                await Task.Delay(80, token).ConfigureAwait(false);
            }
            throw new TimeoutException("页面加载超过 45 秒，请检查外部资源或网络。");
        }

        static Task<object> Prepare(Cdp page, Options o)
        {
            return page.Evaluate("window.__hvl.prepare(" + Cdp.Json.Serialize(new {
                fit = o.Fit, hide = o.Hide, stage = o.Stage, start = o.Start
            }) + ")");
        }

        static async Task<string> CheckAudio(string path, Options o, ProcessJob job, CancellationToken token)
        {
            var info = StartInfo(o.Ffmpeg, new[] {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-i", path,
                "-map", "0:a:0", "-frames:a", "1", "-f", "null", "-"
            });
            info.RedirectStandardError = true;
            info.StandardErrorEncoding = Encoding.UTF8;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var probe = Process.Start(info))
            {
                job.Add(probe);
                timeout.CancelAfter(15000);
                Task<string> error = probe.StandardError.ReadToEndAsync();
                using (timeout.Token.Register(() => { try { if (!probe.HasExited) probe.Kill(); } catch (InvalidOperationException) { } }))
                {
                    while (!probe.HasExited) await Task.Delay(50).ConfigureAwait(false);
                    string detail = await error.ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (timeout.IsCancellationRequested) throw new TimeoutException("音轨检查超过 15 秒：" + path);
                    return probe.ExitCode == 0 ? "" : (string.IsNullOrWhiteSpace(detail) ?
                        "FFmpeg 音轨检查失败，退出码：" + probe.ExitCode : detail.Trim());
                }
            }
        }

        static async Task<string> Audio(Cdp page, Options o, string work, Action<Update> progress, ProcessJob job, CancellationToken token, string[] pageSources)
        {
            if (o.Mute) return "";
            bool explicitAudio = !string.IsNullOrWhiteSpace(o.Audio);
            var sources = explicitAudio ? new[] { new Uri(Path.GetFullPath(o.Audio)).AbsoluteUri } :
                (pageSources ?? ((System.Collections.IList)await page.Evaluate("window.__hvl.audio()").ConfigureAwait(false)).Cast<object>().Select(Convert.ToString).ToArray());
            foreach (string source in sources)
            {
                token.ThrowIfCancellationRequested();
                string path = "";
                if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    int comma = source.IndexOf(',');
                    if (comma > 0 && source.Substring(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                    {
                        path = Path.Combine(work, "embedded.audio");
                        File.WriteAllBytes(path, Convert.FromBase64String(source.Substring(comma + 1)));
                    }
                }
                Uri uri;
                if (Uri.TryCreate(source, UriKind.Absolute, out uri) && uri.IsFile) path = uri.LocalPath;
                if (path.Length == 0)
                {
                    progress(new Update(3, "提示：页面音轨不是本地文件；请在音频栏指定文件：" + source.Substring(0, Math.Min(source.Length, 200))));
                    continue;
                }
                if (!File.Exists(path))
                {
                    progress(new Update(3, "警告：页面音轨文件不存在：" + path));
                    continue;
                }
                string problem = await CheckAudio(path, o, job, token).ConfigureAwait(false);
                if (problem.Length == 0)
                {
                    progress(new Update(3, "使用音轨：" + path));
                    return path;
                }
                if (explicitAudio) throw new IOException("指定文件没有可解码的音轨，请选择有效音频文件：" + path + "\r\n" + problem);
                progress(new Update(3, "跳过无可用音轨的页面媒体：" + path));
            }
            if (sources.Length > 0) progress(new Update(3, "警告：未找到可用音轨，输出将无声；可在音频栏指定文件。"));
            return "";
        }

        // Re-encode a local video into a low-resolution, all-keyframe proxy so the
        // virtual clock can seek it frame-by-frame without re-decoding whole GOPs.
        static async Task<string> ProxyVideo(string path, double duration, Options o, string work, ProcessJob job, CancellationToken token)
        {
            string proxy = Path.Combine(work, "proxy-" + Guid.NewGuid().ToString("N") + ".mp4");
            string vf = string.Format(CultureInfo.InvariantCulture,
                "scale='min(iw,{0})':'min(ih,{1})':force_original_aspect_ratio=decrease,scale='trunc(iw/2)*2':'trunc(ih/2)*2'",
                o.Width, o.Height);
            var args = new List<string> {
                "-hide_banner", "-loglevel", "error", "-y", "-nostdin", "-i", path,
                "-map", "0:v:0", "-vf", vf,
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
                "-g", "1", "-keyint_min", "1", "-sc_threshold", "0",
                "-pix_fmt", "yuv420p", proxy
            };
            var info = StartInfo(o.Ffmpeg, args);
            info.RedirectStandardError = true;
            info.StandardErrorEncoding = Encoding.UTF8;
            int timeoutMs = Math.Max(180000, Math.Min(1800000, (int)Math.Ceiling(duration * 10) * 1000));
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var process = Process.Start(info))
            {
                job.Add(process);
                timeout.CancelAfter(timeoutMs);
                Task<string> error = process.StandardError.ReadToEndAsync();
                using (timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } }))
                {
                    while (!process.HasExited) await Task.Delay(100).ConfigureAwait(false);
                    string detail = await error.ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (timeout.IsCancellationRequested) throw new TimeoutException("视频代理生成超过 " + timeoutMs / 1000 + " 秒：" + path);
                    if (process.ExitCode != 0 || !File.Exists(proxy))
                        throw new IOException("视频代理生成失败：" + (string.IsNullOrWhiteSpace(detail) ? "退出码 " + process.ExitCode : detail.Trim()));
                }
            }
            return proxy;
        }

        internal static async Task<string> ConvertVideo(Options o, Action<Update> progress, CancellationToken token)
        {
            Validate(o);
            string work = Path.Combine(Path.GetTempPath(), "HtmlVideoLite-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            string destination = null;
            BrowserProcess browser = null;
            Process encoder = null;
            Cdp page = null;
            Cdp screenshots = null;
            using (var job = new ProcessJob())
            {
                try
                {
                    browser = await BrowserProcess.Start(o, work, progress, token).ConfigureAwait(false);
                    string pageEndpoint;
                    using (var root = new Cdp(token))
                    {
                        await root.Connect(browser.Endpoint).ConfigureAwait(false);
                        var target = await root.Call("Target.createTarget", new { url = "about:blank" }).ConfigureAwait(false);
                        string targetId = Convert.ToString(target["targetId"]);
                        page = new Cdp(token);
                        var endpoint = new Uri(browser.Endpoint);
                        pageEndpoint = "ws://127.0.0.1:" + endpoint.Port + "/devtools/page/" + targetId;
                        await page.Connect(pageEndpoint).ConfigureAwait(false);
                    }
                    await page.Call("Page.enable", new { }).ConfigureAwait(false);
                    await page.Call("Page.bringToFront", new { }).ConfigureAwait(false);
                    await page.Call("Emulation.setDeviceMetricsOverride", new {
                        width = o.Width, height = o.Height, deviceScaleFactor = 1, mobile = false
                    }).ConfigureAwait(false);
                    await page.Call("Page.addScriptToEvaluateOnNewDocument", new {
                        source = Resource("Clock.js") + "\n" + Resource("Stage.js")
                    }).ConfigureAwait(false);
                    progress(new Update(1, "加载 HTML、字体与图片…"));
                    await Load(page, o, token).ConfigureAwait(false);
                    double duration = o.Duration;
                    if (duration == 0)
                    {
                        await Prepare(page, o).ConfigureAwait(false);
                        var probe = (Dictionary<string, object>)await page.Evaluate("window.__hvl.probe()").ConfigureAwait(false);
                        duration = Convert.ToDouble(probe["duration"], CultureInfo.InvariantCulture);
                        if (duration <= 0) throw new InvalidOperationException("无法可靠识别时长，请填写时长后重试；不会猜测并截断视频。");
                        progress(new Update(2, "识别时长：" + duration.ToString("0.###") + " 秒（" + probe["source"] + "）"));
                        // Probe must never consume the beginning of the actual recording.
                        await Load(page, o, token).ConfigureAwait(false);
                    }
                    var preparation = (Dictionary<string, object>)await Prepare(page, o).ConfigureAwait(false);
                    progress(new Update(3, "舞台：" + preparation["stage"] + "；" + preparation["playback"]));
                    if (Convert.ToInt32(preparation["missing"]) > 0)
                        progress(new Update(3, "警告：存在未加载的图片，请检查资源路径或网络。"));
                    var missing = (System.Collections.IList)await page.Evaluate("window.__hvl.missing()").ConfigureAwait(false);
                    foreach (object resource in missing)
                        progress(new Update(3, "素材未加载：" + resource));
                    string[] pageSources = null;
                    if (!o.Mute)
                    {
                        try { pageSources = ((System.Collections.IList)await page.Evaluate("window.__hvl.audio()").ConfigureAwait(false)).Cast<object>().Select(Convert.ToString).ToArray(); }
                        catch { }
                    }
                    try
                    {
                        var videos = (System.Collections.IList)await page.Evaluate("window.__hvl.videos()").ConfigureAwait(false);
                        var proxy = new Dictionary<string, string>();
                        foreach (object entry in videos)
                        {
                            var video = (Dictionary<string, object>)entry;
                            string src = Convert.ToString(video["src"]);
                            double videoDuration = Convert.ToDouble(video["duration"], CultureInfo.InvariantCulture);
                            if (videoDuration < 3) continue;
                            Uri uri;
                            if (!Uri.TryCreate(src, UriKind.Absolute, out uri) || !uri.IsFile) continue;
                            string path = uri.LocalPath;
                            if (!File.Exists(path)) continue;
                            progress(new Update(3, "生成视频代理（加速寻帧）：" + path));
                            try
                            {
                                string proxied = await ProxyVideo(path, videoDuration, o, work, job, token).ConfigureAwait(false);
                                proxy[src] = new Uri(proxied).AbsoluteUri;
                                progress(new Update(3, "视频代理就绪：" + path));
                            }
                            catch (Exception e)
                            {
                                progress(new Update(3, "视频代理生成失败，改用原视频：" + path + "（" + e.Message.Split('\n')[0] + "）"));
                            }
                        }
                        if (proxy.Count > 0)
                            await page.Evaluate("window.__hvl.applyProxy(" + Cdp.Json.Serialize(proxy) + ")").ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        progress(new Update(3, "视频代理处理跳过：" + e.Message.Split('\n')[0]));
                    }
                    string audio = await Audio(page, o, work, progress, job, token, pageSources).ConfigureAwait(false);
                    if (audio.Length > 0 && string.Equals(audio, o.Output, StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("输出文件不能覆盖页面引用的音视频资源。");
                    int frames = Math.Max(1, (int)Math.Ceiling(duration * o.Fps - 1e-9));
                    double encodedDuration = (double)frames / o.Fps;
                    string temporaryVideo = Path.Combine(work, "render.mp4");
                    progress(new Update(3, o.Fast ? "快速模式：JPEG 95 + veryfast 编码（轻微有损）。" : "精细模式：PNG + medium 编码。"));
                    var args = new List<string> {
                        "-hide_banner", "-loglevel", "error", "-y",
                        "-f", "image2pipe", "-vcodec", o.Fast ? "mjpeg" : "png", "-framerate", o.Fps.ToString(CultureInfo.InvariantCulture), "-i", "pipe:0"
                    };
                    if (audio.Length > 0) args.AddRange(new[] { "-stream_loop", "-1", "-i", audio });
                    args.AddRange(new[] { "-map", "0:v:0" });
                    if (audio.Length > 0) args.AddRange(new[] { "-map", "1:a:0", "-c:a", "aac", "-b:a", "192k" });
                    args.AddRange(new[] {
                        "-c:v", "libx264", "-preset", o.Fast ? "veryfast" : "medium", "-crf", o.Crf.ToString(CultureInfo.InvariantCulture),
                        "-pix_fmt", "yuv420p", "-t", encodedDuration.ToString("0.########", CultureInfo.InvariantCulture),
                        "-movflags", "+faststart", temporaryVideo
                    });
                    var encoderInfo = StartInfo(o.Ffmpeg, args);
                    encoderInfo.RedirectStandardInput = true;
                    encoderInfo.RedirectStandardError = true;
                    encoderInfo.StandardErrorEncoding = Encoding.UTF8;
                    encoder = Process.Start(encoderInfo);
                    job.Add(encoder);
                    Task<string> error = encoder.StandardError.ReadToEndAsync();
                    byte[] first = null;
                    int changed = 0;
                    var watch = Stopwatch.StartNew();
                    screenshots = new Cdp(token);
                    await screenshots.Connect(pageEndpoint).ConfigureAwait(false);
                    bool clipped = false;
                    using (token.Register(() => { try { if (!encoder.HasExited) encoder.Kill(); } catch (InvalidOperationException) { } }))
                    {
                        for (int i = 0; i < frames; i++)
                        {
                            token.ThrowIfCancellationRequested();
                            if (i > 0) await page.Evaluate("window.__hvl.step(" + (1000.0 / o.Fps).ToString("R", CultureInfo.InvariantCulture) + ")").ConfigureAwait(false);
                            var captureOptions = new Dictionary<string, object> {
                                { "format", o.Fast ? "jpeg" : "png" }, { "fromSurface", true },
                                { "captureBeyondViewport", clipped }, { "optimizeForSpeed", o.Fast }
                            };
                            if (clipped) captureOptions["clip"] = new { x = 0, y = 0, width = o.Width, height = o.Height, scale = 1 };
                            if (o.Fast) captureOptions["quality"] = 95;
                            Dictionary<string, object> capture = null;
                            try { capture = await screenshots.Call("Page.captureScreenshot", captureOptions, 15000).ConfigureAwait(false); }
                            catch (TimeoutException)
                            {
                                if (clipped) throw new TimeoutException("第 " + (i + 1) + " 帧截图失败，备用捕获也已超时。请尝试 1280×720、其他浏览器，并检查页面素材。");
                            }
                            if (capture == null)
                            {
                                // A cancelled WebSocket receive cannot be reused. Keep page control on its own connection.
                                screenshots.Dispose();
                                screenshots = new Cdp(token);
                                await screenshots.Connect(pageEndpoint).ConfigureAwait(false);
                                clipped = true;
                                captureOptions["captureBeyondViewport"] = true;
                                captureOptions["clip"] = new { x = 0, y = 0, width = o.Width, height = o.Height, scale = 1 };
                                progress(new Update(3 + i * 92 / frames, "第 " + (i + 1) + " 帧截图超时，重建连接并尝试显式区域捕获…"));
                                try { capture = await screenshots.Call("Page.captureScreenshot", captureOptions, 15000).ConfigureAwait(false); }
                                catch (TimeoutException) { throw new TimeoutException("第 " + (i + 1) + " 帧两种截图方式均超时。请尝试 1280×720、其他浏览器，并检查页面素材。"); }
                            }
                            byte[] png = System.Convert.FromBase64String((string)capture["data"]);
                            if (first == null) first = png;
                            else if (!first.SequenceEqual(png)) changed++;
                            try { await encoder.StandardInput.BaseStream.WriteAsync(png, 0, png.Length, token).ConfigureAwait(false); }
                            catch (IOException)
                            {
                                token.ThrowIfCancellationRequested();
                                if (encoder.HasExited)
                                    throw new IOException("FFmpeg 编码失败：" + error.GetAwaiter().GetResult());
                                throw;
                            }
                            if (i == frames - 1 || watch.ElapsedMilliseconds > 250)
                            {
                                progress(new Update(3 + (i + 1) * 92 / frames, "渲染 " + (i + 1) + " / " + frames + " 帧"));
                                watch.Restart();
                            }
                        }
                        encoder.StandardInput.Close();
                        progress(new Update(96, "编码并封装 MP4…"));
                        while (!encoder.HasExited) await Task.Delay(100, token).ConfigureAwait(false);
                        string detail = await error.ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if (encoder.ExitCode != 0) throw new IOException("FFmpeg 编码失败：" + detail);
                    }
                    var faults = (System.Collections.IList)await page.Evaluate("window.__hvl.faults").ConfigureAwait(false);
                    if (faults.Count > 0) progress(new Update(97, "页面脚本警告：" + string.Join("\r\n", faults.Cast<object>().Select(x => Convert.ToString(x)))));
                    if (changed == 0 && frames > 1) progress(new Update(97, "警告：画面完全静止，请检查播放入口；静态 HTML 可忽略。"));
                    // Release media handles before publishing over an existing output.
                    screenshots.Dispose();
                    screenshots = null;
                    page.Dispose();
                    page = null;
                    job.Dispose();
                    browser.Dispose();
                    browser = null;
                    Directory.CreateDirectory(Path.GetDirectoryName(o.Output));
                    destination = Path.Combine(Path.GetDirectoryName(o.Output), "." + Guid.NewGuid().ToString("N") + ".tmp");
                    // Stage output beside the destination, then publish atomically.
                    using (var input = File.OpenRead(temporaryVideo))
                    using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        await input.CopyToAsync(output, 81920, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (File.Exists(o.Output))
                    {
                        if (!o.Overwrite) throw new IOException("输出路径已被其他程序创建，未覆盖。");
                        File.Replace(destination, o.Output, null);
                    }
                    else File.Move(destination, o.Output);
                    destination = null;
                    string summary = frames + " 帧，" + encodedDuration.ToString("0.###") + " 秒，" +
                        (new FileInfo(o.Output).Length / 1048576.0).ToString("0.00") + " MB，" +
                        (audio.Length == 0 ? "无音轨" : "含音轨");
                    progress(new Update(100, "完成：" + summary));
                    return o.Output;
                }
                finally
                {
                    if (page != null) page.Dispose();
                    if (screenshots != null) screenshots.Dispose();
                    job.Dispose();
                    if (encoder != null) { encoder.WaitForExit(5000); encoder.Dispose(); }
                    if (browser != null) browser.Dispose();
                    if (destination != null) File.Delete(destination);
                    // All cleanup targets are private paths allocated by this conversion.
                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        try { Directory.Delete(work, true); break; }
                        catch (IOException) { if (attempt == 4) progress(new Update(100, "临时目录暂时被占用：" + work)); }
                        catch (UnauthorizedAccessException) { if (attempt == 4) progress(new Update(100, "临时目录暂时被占用：" + work)); }
                        Thread.Sleep(200);
                    }
                }
            }
        }
    }

    // A private Windows job closes only the browser/encoder process trees we own.
    internal sealed class ProcessJob : IDisposable
    {
        IntPtr handle;
        [StructLayout(LayoutKind.Sequential)]
        struct BasicLimits
        {
            public long PerProcessTime, PerJobTime;
            public uint Flags;
            public UIntPtr Minimum, Maximum;
            public uint ActiveProcesses;
            public UIntPtr Affinity;
            public uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)]
        struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateJobObject(IntPtr security, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetInformationJobObject(IntPtr job, int kind, ref ExtendedLimits info, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);
        internal ProcessJob()
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            var limits = new ExtendedLimits();
            limits.Basic.Flags = 0x2000;
            if (handle == IntPtr.Zero || !SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf(limits)))
            {
                int code = Marshal.GetLastWin32Error();
                Dispose();
                throw new System.ComponentModel.Win32Exception(code);
            }
        }
        internal void Add(Process process)
        {
            if (!AssignProcessToJobObject(handle, process.Handle))
            {
                int code = Marshal.GetLastWin32Error();
                if (!process.HasExited) process.Kill();
                throw new System.ComponentModel.Win32Exception(code, "无法隔离子进程");
            }
        }
        public void Dispose()
        {
            if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; }
        }
    }
}
