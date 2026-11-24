using System;
using System.Collections;
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
        internal int Width = 1920, Height = 1080, Fps = 30, Crf = 18, Workers;
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

        // JPEG quality for the fast mode: high enough that the H.264 pass leaves it
        // invisible, low enough that the browser's encoder keeps up.
        internal const int JpegQuality = 95;

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
            if (o.Workers < 0 || o.Workers > 16)
                throw new ArgumentException("并行进程数应为 0（自动）或 1–16。");
            if (string.IsNullOrWhiteSpace(o.Browser)) o.Browser = Browser();
            if (string.IsNullOrWhiteSpace(o.Ffmpeg)) o.Ffmpeg = Ffmpeg();
            if (!File.Exists(o.Browser)) throw new IOException("未找到 Edge/Chrome，请指定浏览器路径。");
            if (!File.Exists(o.Ffmpeg)) throw new IOException("未找到 FFmpeg，请选择 ffmpeg.exe，或将它放到程序旁的 tools 文件夹。");
        }

        // Splitting the timeline across browser processes is the main speedup:
        // capture latency dominates, and each process has its own compositor.
        static int WorkerCount(Options o, int frames)
        {
            int ceiling = Math.Max(1, Math.Min(6, Environment.ProcessorCount / 2));
            int wanted = o.Workers > 0 ? Math.Min(o.Workers, 16) :
                Math.Min(ceiling, Math.Max(1, (frames + 119) / 120));
            // A segment shorter than ~30 frames mostly pays startup and replay cost.
            int byBudget = Math.Max(1, frames / 30);
            return Math.Max(1, Math.Min(Math.Min(wanted, byBudget), Math.Max(1, frames)));
        }

        static string PrepareJson(Options o)
        {
            return Cdp.Json.Serialize(new {
                fit = o.Fit, hide = o.Hide, stage = o.Stage, start = o.Start, fps = o.Fps
            });
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

        static async Task<string> Audio(Cdp page, Options o, string work, Action<Update> progress, ProcessJob job, CancellationToken token)
        {
            if (o.Mute) return "";
            bool explicitAudio = !string.IsNullOrWhiteSpace(o.Audio);
            var sources = explicitAudio ? new[] { new Uri(Path.GetFullPath(o.Audio)).AbsoluteUri } :
                ((IList)await page.Evaluate("window.__hvl.audio()").ConfigureAwait(false)).Cast<object>().Select(Convert.ToString).ToArray();
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

        // Dump a local video to a JPEG frame sequence so the virtual clock can show the
        // correct frame without live-decoding or seeking a real <video> element.
        static async Task<int> DumpFrames(string path, string dir, int fps, Options o, ProcessJob job, CancellationToken token)
        {
            var args = new List<string> {
                "-hide_banner", "-loglevel", "error", "-y", "-nostdin", "-i", path,
                "-vf", "fps=" + fps.ToString(CultureInfo.InvariantCulture) +
                       ",scale=" + o.Width + ":" + o.Height + ":force_original_aspect_ratio=increase,crop=" + o.Width + ":" + o.Height,
                "-start_number", "0", "-q:v", "2", Path.Combine(dir, "frame_%05d.jpg")
            };
            var info = StartInfo(o.Ffmpeg, args);
            info.RedirectStandardError = true;
            info.StandardErrorEncoding = Encoding.UTF8;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var process = Process.Start(info))
            {
                job.Add(process);
                timeout.CancelAfter(300000);
                Task<string> error = process.StandardError.ReadToEndAsync();
                using (timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } }))
                {
                    while (!process.HasExited) await Task.Delay(100).ConfigureAwait(false);
                    string detail = await error.ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (timeout.IsCancellationRequested) throw new TimeoutException("视频抽帧超过 300 秒：" + path);
                    if (process.ExitCode != 0)
                        throw new IOException("视频抽帧失败：" + (string.IsNullOrWhiteSpace(detail) ? "退出码 " + process.ExitCode : detail.Trim()));
                }
            }
            return Directory.GetFiles(dir, "frame_*.jpg").Length;
        }

        static async Task<string> FrameMap(Cdp page, Options o, string work, Action<Update> progress, ProcessJob job, CancellationToken token)
        {
            var map = new Dictionary<string, Dictionary<string, object>>();
            try
            {
                var videos = (IList)await page.Evaluate("window.__hvl.videos()").ConfigureAwait(false);
                int slot = 0;
                foreach (object entry in videos)
                {
                    token.ThrowIfCancellationRequested();
                    string src = Convert.ToString(entry);
                    Uri uri;
                    if (!Uri.TryCreate(src, UriKind.Absolute, out uri) || !uri.IsFile) continue;
                    string path = uri.LocalPath;
                    if (!File.Exists(path)) continue;
                    string dir = Path.Combine(work, "frames-" + (slot++));
                    Directory.CreateDirectory(dir);
                    progress(new Update(3, "抽取视频帧（避免逐帧寻帧卡死）：" + path));
                    try
                    {
                        int count = await DumpFrames(path, dir, o.Fps, o, job, token).ConfigureAwait(false);
                        if (count == 0) continue;
                        map[src] = new Dictionary<string, object> {
                            { "base", new Uri(dir).AbsoluteUri + "/frame_" },
                            { "fps", o.Fps }, { "count", count }
                        };
                    }
                    catch (Exception e)
                    {
                        if (e is OperationCanceledException) throw;
                        progress(new Update(3, "视频抽帧失败，改用逐帧寻帧：" + path + "（" + e.Message.Split('\n')[0] + "）"));
                    }
                }
            }
            catch (Exception e)
            {
                if (e is OperationCanceledException) throw;
                progress(new Update(3, "视频抽帧处理跳过：" + e.Message.Split('\n')[0]));
                return "";
            }
            return map.Count == 0 ? "" : Cdp.Json.Serialize(map);
        }

        // One ordered concat plus audio mix: segments were encoded with identical
        // parameters, so the video stream can be copied instead of re-encoded.
        static async Task Mux(Options o, List<string> segments, string audio, double duration, string output, ProcessJob job, CancellationToken token)
        {
            if (segments.Count == 0) throw new IOException("没有可用的渲染分片。");
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-nostdin" };
            if (segments.Count > 1)
            {
                string list = Path.Combine(Path.GetDirectoryName(output), "segments.txt");
                var text = new StringBuilder();
                foreach (string segment in segments)
                    text.Append("file '").Append(segment.Replace("\\", "/").Replace("'", "'\\''")).Append("'\r\n");
                File.WriteAllText(list, text.ToString(), new UTF8Encoding(false));
                args.AddRange(new[] { "-f", "concat", "-safe", "0", "-i", list });
            }
            else args.AddRange(new[] { "-i", segments[0] });
            if (audio.Length > 0) args.AddRange(new[] { "-stream_loop", "-1", "-i", audio });
            args.AddRange(new[] { "-map", "0:v:0" });
            if (audio.Length > 0) args.AddRange(new[] { "-map", "1:a:0", "-c:a", "aac", "-b:a", "192k" });
            args.AddRange(new[] {
                "-c:v", "copy", "-t", duration.ToString("0.########", CultureInfo.InvariantCulture),
                "-movflags", "+faststart", output
            });
            var info = StartInfo(o.Ffmpeg, args);
            info.RedirectStandardError = true;
            info.StandardErrorEncoding = Encoding.UTF8;
            using (var process = Process.Start(info))
            {
                job.Add(process);
                Task<string> error = process.StandardError.ReadToEndAsync();
                using (token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } }))
                {
                    while (!process.HasExited) await Task.Delay(60, token).ConfigureAwait(false);
                    string detail = await error.ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0)
                        throw new IOException("合并分片/封装失败：" + (string.IsNullOrWhiteSpace(detail) ? "退出码 " + process.ExitCode : detail.Trim()));
                }
            }
        }

        internal static async Task<string> ConvertVideo(Options o, Action<Update> progress, CancellationToken token)
        {
            Validate(o);
            // Workers report from several threads; serialise the callback.
            var sync = new object();
            var sink = progress;
            var wall = Stopwatch.StartNew();
            bool timing = Environment.GetEnvironmentVariable("H2V_TIMING") != null;
            progress = u => {
                lock (sync) sink(timing ? new Update(u.Percent, "[" + wall.ElapsedMilliseconds + "ms] " + u.Message) : u);
            };
            string work = Path.Combine(Path.GetTempPath(), "HtmlVideoLite-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            string destination = null;
            string temporaryVideo = Path.Combine(work, "render.mp4");
            var workers = new List<Worker>();
            var encoders = new ProcessJob();
            try
            {
                var first = new Worker(o, work, 0, token, encoders);
                workers.Add(first);
                progress(new Update(0, "启动浏览器…"));

                double duration = o.Duration;
                int frames, count;
                if (duration > 0)
                {
                    // The timeline is known, so every process can start and load at the
                    // same time instead of waiting for the first page.
                    frames = Math.Max(1, (int)Math.Ceiling(duration * o.Fps - 1e-9));
                    count = WorkerCount(o, frames);
                    var starts = Spawn(o, work, workers, encoders, count, progress, token);
                    starts.Add(LaunchAndLoad(first, progress));
                    if (count > 1) progress(new Update(2, "并行渲染：" + count + " 个浏览器进程，各渲染一段…"));
                    else progress(new Update(1, "加载 HTML、字体与图片…"));
                    await Task.WhenAll(starts).ConfigureAwait(false);
                }
                else
                {
                    await first.Launch(progress).ConfigureAwait(false);
                    await first.Load().ConfigureAwait(false);
                    await first.Prepare().ConfigureAwait(false);
                    var probe = (Dictionary<string, object>)await first.Page.Evaluate("window.__hvl.probe()").ConfigureAwait(false);
                    duration = Convert.ToDouble(probe["duration"], CultureInfo.InvariantCulture);
                    if (duration <= 0) throw new InvalidOperationException("无法可靠识别时长，请填写时长后重试；不会猜测并截断视频。");
                    progress(new Update(2, "识别时长：" + duration.ToString("0.###") + " 秒（" + probe["source"] + "）"));
                    // Probe must never consume the beginning of the actual recording.
                    await first.Load().ConfigureAwait(false);
                    frames = Math.Max(1, (int)Math.Ceiling(duration * o.Fps - 1e-9));
                    count = WorkerCount(o, frames);
                    var starts = Spawn(o, work, workers, encoders, count, progress, token);
                    if (count > 1) progress(new Update(2, "并行渲染：" + count + " 个浏览器进程，各渲染一段…"));
                    await Task.WhenAll(starts).ConfigureAwait(false);
                }
                double encodedDuration = (double)frames / o.Fps;

                await Task.WhenAll(workers.Select(w => w.Prepare())).ConfigureAwait(false);
                var preparation = workers[0].Preparation;
                progress(new Update(3, "舞台：" + preparation["stage"] + "；" + preparation["playback"]));
                // A page whose play entry cannot be found never starts, and the only
                // symptom would be a 60-second still of the cover. Say so up front,
                // while the user can still cancel and fill in the selector.
                bool started = preparation.ContainsKey("clicked") && Convert.ToBoolean(preparation["clicked"]);
                if (!started && string.IsNullOrWhiteSpace(o.Start))
                    progress(new Update(3, "未识别到播放入口（未点击）。若页面需要点击才开始，请填写“播放选择器”（CSS，例如 #startBtn，也可先点页面看按钮的 id）；静态 HTML 可忽略。"));
                if (Convert.ToInt32(preparation["missing"]) > 0)
                    progress(new Update(3, "警告：存在未加载的图片，请检查资源路径或网络。"));
                var missing = (IList)await workers[0].Page.Evaluate("window.__hvl.missing()").ConfigureAwait(false);
                foreach (object resource in missing)
                    progress(new Update(3, "素材未加载：" + resource));

                string audio = await Audio(workers[0].Page, o, work, progress, encoders, token).ConfigureAwait(false);
                if (audio.Length > 0 && string.Equals(audio, o.Output, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("输出文件不能覆盖页面引用的音视频资源。");

                string frameMap = await FrameMap(workers[0].Page, o, work, progress, encoders, token).ConfigureAwait(false);
                var applies = new List<Task>();
                foreach (var worker in workers)
                {
                    worker.FrameMapJson = frameMap;
                    applies.Add(worker.ApplyFrames());
                }
                await Task.WhenAll(applies).ConfigureAwait(false);

                int per = (frames + count - 1) / count;
                for (int i = 0; i < workers.Count; i++)
                {
                    workers[i].FirstFrame = Math.Min(frames, i * per);
                    workers[i].LastFrame = Math.Min(frames, (i + 1) * per);
                }
                var jumps = new List<Task>();
                foreach (var worker in workers)
                    if (worker.FirstFrame > 0) jumps.Add(worker.Jump(worker.FirstFrame));
                if (jumps.Count > 0) progress(new Update(3, "快进到各分片起点…"));
                await Task.WhenAll(jumps).ConfigureAwait(false);

                progress(new Update(3, o.Fast ? "快速模式：JPEG 95 + veryfast 编码（轻微有损）。" : "精细模式：PNG + medium 编码。"));
                var gate = new ProgressGate(progress, frames, count);
                await Task.WhenAll(workers.Where(w => w.LastFrame > w.FirstFrame)
                    .Select(w => w.Render(gate, progress))).ConfigureAwait(false);

                var faults = new List<string>();
                foreach (var worker in workers)
                {
                    try
                    {
                        foreach (object item in (IList)await worker.Page.Evaluate("window.__hvl.faults").ConfigureAwait(false))
                            faults.Add(Convert.ToString(item));
                    }
                    catch (Exception e) { if (e is OperationCanceledException) throw; }
                }
                faults = faults.Distinct().ToList();
                if (faults.Count > 0) progress(new Update(97, "页面脚本警告：" + string.Join("\r\n", faults)));
                int changed = workers.Sum(w => w.Changed);
                if (changed == 0 && frames > 1)
                    progress(new Update(97, started
                        ? "警告：画面完全静止，请检查播放入口；静态 HTML 可忽略。"
                        : "警告：画面完全静止，且没有点到播放入口。请在“播放选择器”填写开始按钮的 CSS（例如 #startBtn），或用 --start 指定后重试；静态 HTML 可忽略。"));

                var segments = workers.Where(w => w.LastFrame > w.FirstFrame).Select(w => w.Segment).ToList();
                progress(new Update(96, segments.Count > 1 ? "合并 " + segments.Count + " 个分片并封装 MP4…" : "封装 MP4…"));
                await Mux(o, segments, audio, encodedDuration, temporaryVideo, encoders, token).ConfigureAwait(false);

                // Release media handles before publishing over an existing output.
                DisposeAll(workers);
                encoders.Dispose();
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
                DisposeAll(workers);
                encoders.Dispose();
                if (destination != null) { try { File.Delete(destination); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
                // All cleanup targets are private paths allocated by this conversion.
                // A just-killed renderer can hold its profile file handles briefly.
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    try { Directory.Delete(work, true); break; }
                    catch (IOException) { if (attempt == 9) progress(new Update(100, "临时目录暂时被占用：" + work)); }
                    catch (UnauthorizedAccessException) { if (attempt == 9) progress(new Update(100, "临时目录暂时被占用：" + work)); }
                    Thread.Sleep(300);
                }
            }
        }

        static async Task LaunchAndLoad(Worker worker, Action<Update> progress)
        {
            await worker.Launch(progress).ConfigureAwait(false);
            await worker.Load().ConfigureAwait(false);
        }

        static List<Task> Spawn(Options o, string work, List<Worker> workers, ProcessJob encoders,
            int count, Action<Update> progress, CancellationToken token)
        {
            var starts = new List<Task>();
            for (int i = 1; i < count; i++)
            {
                var worker = new Worker(o, work, i, token, encoders);
                workers.Add(worker);
                starts.Add(LaunchAndLoad(worker, progress));
            }
            return starts;
        }

        // Browsers take seconds to exit; waiting for them one by one only lengthens
        // the tail of the conversion.
        static void DisposeAll(List<Worker> workers)
        {
            var tasks = new List<Task>();
            foreach (var worker in workers) tasks.Add(Task.Run(() => worker.Dispose()));
            try { Task.WaitAll(tasks.ToArray(), 15000); }
            catch (AggregateException) { }
        }

        internal sealed class ProgressGate
        {
            readonly Action<Update> report;
            readonly int total, workers;
            readonly Stopwatch watch = Stopwatch.StartNew();
            int done;

            internal ProgressGate(Action<Update> report, int total, int workers)
            {
                this.report = report; this.total = total; this.workers = workers;
            }

            internal void Frame()
            {
                int n = Interlocked.Increment(ref done);
                bool tick;
                lock (watch) { tick = watch.ElapsedMilliseconds > 250; if (tick) watch.Restart(); }
                if (tick || n == total) Report(Math.Min(95, 3 + n * 92 / Math.Max(1, total)),
                    "渲染 " + n + " / " + total + " 帧" + (workers > 1 ? "（" + workers + " 进程并行）" : ""));
            }

            internal void Note(string message)
            {
                // Diagnostics must survive the command line's progress filter.
                Report(3, message);
            }

            internal void Report(int percent, string message) { report(new Update(percent, message)); }
        }

        // One browser process rendering one contiguous frame range. Each range is
        // encoded on its own, then concatenated without re-encoding.
        internal sealed class Worker : IDisposable
        {
            readonly Options options;
            readonly string profileRoot;
            readonly CancellationToken token;
            readonly ProcessJob encoders;
            readonly int index;
            BrowserProcess browser;
            Cdp page, shots;
            Screencast cast;
            string endpoint;
            bool streamMode;
            int misses, rebuilds;
            internal int FirstFrame, LastFrame, Changed;
            internal long ReplayMs, StepMs, CaptureMs, StreamMs, LaunchMs, LoadMs, Streamed, Stale;
            internal string Segment, FrameMapJson = "";
            internal Dictionary<string, object> Preparation;

            internal Worker(Options options, string work, int index, CancellationToken token, ProcessJob encoders)
            {
                this.options = options; this.index = index;
                this.token = token; this.encoders = encoders;
                profileRoot = Path.Combine(work, "w" + index);
            }

            internal Cdp Page { get { return page; } }

            internal async Task Launch(Action<Update> progress)
            {
                var watch = Stopwatch.StartNew();
                browser = await BrowserProcess.Start(options, profileRoot, progress, token).ConfigureAwait(false);
                await Open().ConfigureAwait(false);
                LaunchMs = watch.ElapsedMilliseconds;
            }

            async Task Open()
            {
                string targetId;
                using (var root = new Cdp(token))
                {
                    await root.Connect(browser.Endpoint).ConfigureAwait(false);
                    var target = await root.Call("Target.createTarget", new { url = "about:blank" }).ConfigureAwait(false);
                    targetId = Convert.ToString(target["targetId"]);
                }
                var uri = new Uri(browser.Endpoint);
                endpoint = "ws://127.0.0.1:" + uri.Port + "/devtools/page/" + targetId;
                page = new Cdp(token);
                await page.Connect(endpoint).ConfigureAwait(false);
                await page.Call("Page.enable", new { }).ConfigureAwait(false);
                await page.Call("Page.bringToFront", new { }).ConfigureAwait(false);
                await page.Call("Emulation.setDeviceMetricsOverride", new {
                    width = options.Width, height = options.Height, deviceScaleFactor = 1, mobile = false
                }).ConfigureAwait(false);
                await page.Call("Page.addScriptToEvaluateOnNewDocument", new {
                    source = Resource("Clock.js") + "\n" + Resource("Stage.js")
                }).ConfigureAwait(false);
                // Captures run on their own connection so a cancelled receive never
                // takes the page-control socket down with it.
                shots = new Cdp(token);
                await shots.Connect(endpoint).ConfigureAwait(false);
            }

            // Started only when a requested capture stalls: pushing frames costs the
            // browser a JPEG encode per presented frame, so normal pages must not pay
            // for it. Once started, the stream carries the rest of the segment.
            async Task<bool> StartStream()
            {
                if (cast != null) return true;
                try
                {
                    cast = new Screencast(token);
                    await cast.Start(endpoint, options.Width, options.Height, JpegQuality).ConfigureAwait(false);
                    return true;
                }
                catch (Exception e)
                {
                    if (e is OperationCanceledException) throw;
                    if (cast != null) { cast.Dispose(); cast = null; }
                    return false;
                }
            }

            void StopStream()
            {
                if (cast != null) { cast.Dispose(); cast = null; }
            }

            async Task<byte[]> StreamFrame(int timeoutMs)
            {
                if (cast == null) return null;
                cast.Drain();
                // Only a real compositor frame proves the current DOM has been drawn.
                await page.Evaluate("window.__hvl.settle(2)").ConfigureAwait(false);
                var watch = Stopwatch.StartNew();
                byte[] frame = await cast.Next(timeoutMs).ConfigureAwait(false);
                StreamMs += watch.ElapsedMilliseconds;
                if (frame != null) Streamed++;
                return frame;
            }

            internal async Task Load()
            {
                var watch = Stopwatch.StartNew();
                var navigation = await page.Call("Page.navigate", new { url = new Uri(options.Input).AbsoluteUri }).ConfigureAwait(false);
                if (navigation.ContainsKey("errorText")) throw new IOException("HTML 加载失败：" + navigation["errorText"]);
                while (watch.ElapsedMilliseconds < 45000)
                {
                    token.ThrowIfCancellationRequested();
                    object state = await page.Evaluate("document.readyState").ConfigureAwait(false);
                    if (Convert.ToString(state) == "complete") { LoadMs += watch.ElapsedMilliseconds; return; }
                    await Task.Delay(80, token).ConfigureAwait(false);
                }
                throw new TimeoutException("页面加载超过 45 秒，请检查外部资源或网络。");
            }

            internal async Task Prepare()
            {
                Preparation = (Dictionary<string, object>)await page.Evaluate(
                    "window.__hvl.prepare(" + PrepareJson(options) + ")").ConfigureAwait(false);
            }

            internal async Task ApplyFrames()
            {
                if (FrameMapJson.Length == 0) return;
                await page.Evaluate("window.__hvl.useFrames(" + FrameMapJson + ")").ConfigureAwait(false);
                // Video elements just lost their source; settle before the first capture.
                await page.Evaluate("window.__hvl.step(0)").ConfigureAwait(false);
            }

            internal async Task Jump(int frame)
            {
                double ms = frame * 1000.0 / options.Fps;
                var watch = Stopwatch.StartNew();
                await page.Evaluate("window.__hvl.jump(" + ms.ToString("R", CultureInfo.InvariantCulture) + ")",
                    600000).ConfigureAwait(false);
                ReplayMs += watch.ElapsedMilliseconds;
            }

            // A stalled compositor cannot be revived from the outside: rebuild the
            // page and replay the virtual timeline to the frame we were on.
            async Task Replay(int frame)
            {
                StopStream();
                streamMode = false;
                if (shots != null) { shots.Dispose(); shots = null; }
                if (page != null) { page.Dispose(); page = null; }
                await Open().ConfigureAwait(false);
                await Load().ConfigureAwait(false);
                if (FrameMapJson.Length > 0) await ApplyFrames().ConfigureAwait(false);
                await Prepare().ConfigureAwait(false);
                await Jump(frame).ConfigureAwait(false);
            }

            async Task<bool> StepOnce()
            {
                // Never waits for a compositor frame: Page.captureScreenshot forces
                // its own commit, and the pushed-frame path settles only when it is
                // actually about to read a frame. Otherwise a page that changes
                // nothing would pay a vsync per frame for no reason.
                object state = await page.Evaluate("window.__hvl.step(" +
                    (1000.0 / options.Fps).ToString("R", CultureInfo.InvariantCulture) + ", 0)").ConfigureAwait(false);
                var clock = state as Dictionary<string, object>;
                return clock != null && clock.ContainsKey("idle") && Convert.ToBoolean(clock["idle"]);
            }



            // Requested frame: Page.captureScreenshot forces the compositor to draw,
            // so a heavy page can stall it. That is what the pushed stream is for.
            async Task<byte[]> CaptureFrame(int frame, Dictionary<string, object> captureOptions, ProgressGate gate)
            {
                for (int struck = 0; ; struck++)
                {
                    // The 96a0c29 flicker fix: transforms and opacity changes only
                    // reach the compositor surface after a real frame, so grabbing it
                    // without this wait sometimes returns the previous picture.
                    await page.Evaluate("window.__hvl.settle(2)").ConfigureAwait(false);
                    Dictionary<string, object> capture = null;
                    Exception stall = null;
                    var watch = Stopwatch.StartNew();
                    try { capture = await shots.Call("Page.captureScreenshot", captureOptions, 4000).ConfigureAwait(false); }
                    catch (TimeoutException e) { stall = e; }
                    CaptureMs += watch.ElapsedMilliseconds;
                    if (stall == null) return Convert.FromBase64String((string)capture["data"]);
                    token.ThrowIfCancellationRequested();
                    // First stall: let the compositor push frames instead of asking for
                    // them, which is what keeps an Electron offscreen renderer alive.
                    if (!streamMode && await StartStream().ConfigureAwait(false))
                    {
                        streamMode = true;
                        misses = 0;
                        gate.Note("第 " + (frame + 1) + " 帧截图超过 4 秒，改用合成器推送的帧继续渲染。");
                        byte[] pushed = await StreamFrame(6000).ConfigureAwait(false);
                        if (pushed != null) return pushed;
                        gate.Note("推送帧同样没有响应，重建页面并重放…");
                        await Replay(frame).ConfigureAwait(false);
                        continue;
                    }
                    if (rebuilds >= 2) throw new TimeoutException("第 " + (frame + 1) +
                        " 帧截图与推送帧都没有响应，渲染无法继续。请尝试降低分辨率或帧率，或关闭“快速模式”。");
                    // Rebuild, replay to this frame, then capture again; nothing is skipped.
                    rebuilds++;
                    gate.Note("第 " + (frame + 1) + " 帧截图超时，重建页面并重放…");
                    await Replay(frame).ConfigureAwait(false);
                    // A rebuilt surface sometimes only answers a region capture.
                    captureOptions["clip"] = new { x = 0, y = 0, width = options.Width, height = options.Height, scale = 1 };
                }
            }

            internal async Task Render(ProgressGate gate, Action<Update> progress)
            {
                Segment = Path.Combine(profileRoot, "segment-" + index + ".mp4");
                var args = new List<string> {
                    "-hide_banner", "-loglevel", "error", "-y", "-nostdin",
                    "-f", "image2pipe", "-vcodec", options.Fast ? "mjpeg" : "png",
                    "-framerate", options.Fps.ToString(CultureInfo.InvariantCulture), "-i", "pipe:0",
                    "-c:v", "libx264", "-preset", options.Fast ? "veryfast" : "medium",
                    "-crf", options.Crf.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p", Segment
                };
                var info = StartInfo(options.Ffmpeg, args);
                info.RedirectStandardInput = info.RedirectStandardError = true;
                info.StandardErrorEncoding = Encoding.UTF8;
                var encoder = Process.Start(info);
                try
                {
                    encoders.Add(encoder);
                    Task<string> error = encoder.StandardError.ReadToEndAsync();
                    await Loop(encoder, gate).ConfigureAwait(false);
                    encoder.StandardInput.Close();
                    while (!encoder.HasExited) await Task.Delay(60, token).ConfigureAwait(false);
                    string detail = await error.ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (encoder.ExitCode != 0) throw new IOException("FFmpeg 编码失败：" + detail);
                    if (Environment.GetEnvironmentVariable("H2V_TIMING") != null)
                        progress(new Update(3, "计时 分片" + index + "：" + (LastFrame - FirstFrame) + " 帧（推送 " +
                            Streamed + "，滞后重取 " + Stale + "），截图 " + CaptureMs + "ms，等待推送 " + StreamMs +
                            "ms，时钟 " + StepMs + "ms，快进 " + ReplayMs + "ms，启动 " + LaunchMs + "ms，加载 " + LoadMs + "ms"));
                }
                finally
                {
                    try { if (!encoder.HasExited) encoder.Kill(); } catch (InvalidOperationException) { }
                    encoder.Dispose();
                }
            }

            async Task Loop(Process encoder, ProgressGate gate)
            {
                byte[] previous = null, first = null;
                var captureOptions = new Dictionary<string, object> {
                    { "format", options.Fast ? "jpeg" : "png" }, { "fromSurface", true },
                    // captureBeyondViewport forces a full-page composite, which is the
                    // slowest path on heavy pages; the clip below already pins the viewport.
                    { "captureBeyondViewport", false }, { "optimizeForSpeed", options.Fast }
                };
                if (options.Fast) captureOptions["quality"] = JpegQuality;
                for (int i = FirstFrame; i < LastFrame; i++)
                {
                    token.ThrowIfCancellationRequested();
                    bool idle = false, clockStalled = false;
                    if (i > FirstFrame)
                    {
                        var stepWatch = Stopwatch.StartNew();
                        try { idle = await StepOnce().ConfigureAwait(false); }
                        catch (TimeoutException) { clockStalled = true; }
                        StepMs += stepWatch.ElapsedMilliseconds;
                    }
                    if (clockStalled)
                    {
                        // The clock itself stalled: replay this frame instead of skipping it.
                        gate.Note("第 " + (i + 1) + " 帧时钟推进超时，重建页面并重放…");
                        await Replay(i).ConfigureAwait(false);
                        idle = false;
                    }
                    bool reuse = idle && previous != null;
                    byte[] frame;
                    if (reuse) frame = previous;
                    else
                    {
                        frame = null;
                        if (streamMode)
                        {
                            // The first pushed frame after a jump pays for rasterising
                            // the new state; later ones arrive in tens of milliseconds.
                            frame = await StreamFrame(Streamed == 0 ? 6000 : 2000).ConfigureAwait(false);
                            if (frame != null)
                            {
                                misses = 0;
                                // The compositor can still be one presentation behind,
                                // which shows up as the previous picture pushed again.
                                // Ask for an explicit frame instead of baking in a repeat.
                                if (previous != null && frame.SequenceEqual(previous))
                                {
                                    frame = null;
                                    Stale++;
                                }
                            }
                            else if (++misses >= 3)
                            {
                                // Nothing is being pushed (static picture or dead stream).
                                StopStream();
                                streamMode = false;
                                gate.Note("推送帧连续无响应，恢复请求式截图。");
                            }
                        }
                        if (frame == null) frame = await CaptureFrame(i, captureOptions, gate).ConfigureAwait(false);
                    }
                    if (!reuse) previous = frame;
                    if (first == null) first = frame;
                    else if (!first.SequenceEqual(frame)) Changed++;
                    try { await encoder.StandardInput.BaseStream.WriteAsync(frame, 0, frame.Length, token).ConfigureAwait(false); }
                    catch (IOException)
                    {
                        token.ThrowIfCancellationRequested();
                        throw new IOException("FFmpeg 编码进程已退出，无法继续写入第 " + (i + 1) + " 帧。");
                    }
                    gate.Frame();
                }
            }

            public void Dispose()
            {
                if (cast != null) { cast.Dispose(); cast = null; }
                if (shots != null) { shots.Dispose(); shots = null; }
                if (page != null) { page.Dispose(); page = null; }
                if (browser != null) { browser.Dispose(); browser = null; }
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
