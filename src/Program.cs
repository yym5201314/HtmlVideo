using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HtmlVideoLite
{
    internal static class Program
    {
        internal const string APP_VERSION = "1.0.4";

        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 0 || (args.Length == 1 && !args[0].StartsWith("--")))
            {
                Application.Run(new MainForm(args.Length == 1 ? args[0] : ""));
                return 0;
            }
            int reportIndex = Array.IndexOf(args, "--report");
            string report = reportIndex >= 0 && reportIndex + 1 < args.Length ? args[reportIndex + 1] : null;
            var updates = new List<string>();
            try
            {
                var values = new Dictionary<string, string>();
                var flags = new HashSet<string> { "--mute", "--no-fit", "--keep-controls", "--overwrite", "--help", "--precise" };
                for (int i = 0; i < args.Length; i++)
                {
                    if (!args[i].StartsWith("--") || values.ContainsKey(args[i]))
                        throw new ArgumentException("参数无效或重复：" + args[i]);
                    if (flags.Contains(args[i])) values.Add(args[i], "true");
                    else
                    {
                        string key = args[i];
                        if (++i == args.Length) throw new ArgumentException("参数缺少值：" + key);
                        values.Add(key, args[i]);
                    }
                }
                var allowed = new HashSet<string>(flags.Concat(new[] {
                    "--input","--output","--duration","--width","--height","--fps","--crf",
                    "--audio","--browser","--ffmpeg","--stage","--start","--report"
                }));
                foreach (string key in values.Keys)
                    if (!allowed.Contains(key)) throw new ArgumentException("未知参数：" + key);
                report = Get(values, "--report", "");
                if (values.ContainsKey("--help"))
                {
                    updates.Add("HtmlVideoLite --input page.html [--output video.mp4] [--duration 10] [--width 1280 --height 720 --fps 24 --crf 18] [--precise] [--audio bgm.mp3 | --mute] [--stage #stage --start #play] [--no-fit --keep-controls --overwrite] [--browser path --ffmpeg path] [--report result.json]");
                    SaveReport(report, true, "", updates, "");
                    return 0;
                }
                var o = new Options {
                    Input = Get(values, "--input", ""), Output = Get(values, "--output", ""),
                    Duration = double.Parse(Get(values, "--duration", "0"), CultureInfo.InvariantCulture),
                    Width = int.Parse(Get(values, "--width", "1280"), CultureInfo.InvariantCulture),
                    Height = int.Parse(Get(values, "--height", "720"), CultureInfo.InvariantCulture),
                    Fps = int.Parse(Get(values, "--fps", "24"), CultureInfo.InvariantCulture),
                    Crf = int.Parse(Get(values, "--crf", "18"), CultureInfo.InvariantCulture),
                    Audio = Get(values, "--audio", ""), Browser = Get(values, "--browser", ""),
                    Ffmpeg = Get(values, "--ffmpeg", ""), Stage = Get(values, "--stage", ""),
                    Start = Get(values, "--start", ""), Fit = !values.ContainsKey("--no-fit"),
                    Hide = !values.ContainsKey("--keep-controls"), Mute = values.ContainsKey("--mute"),
                    Overwrite = values.ContainsKey("--overwrite"), Fast = !values.ContainsKey("--precise")
                };
                string output = Engine.ConvertVideo(o, u => {
                    if (u.Percent <= 3 || u.Percent >= 96) updates.Add(u.Message);
                    Console.WriteLine(u.Percent + "% " + u.Message);
                }, CancellationToken.None).GetAwaiter().GetResult();
                SaveReport(report, true, output, updates, "");
                return 0;
            }
            catch (Exception e)
            {
                SaveReport(report, false, "", updates, e.Message);
                return 1;
            }
        }

        static string Get(Dictionary<string, string> args, string key, string fallback)
        {
            string value;
            return args.TryGetValue(key, out value) ? value : fallback;
        }

        static void SaveReport(string path, bool success, string output, List<string> updates, string error)
        {
            if (!string.IsNullOrEmpty(path))
                File.WriteAllText(path, Cdp.Json.Serialize(new { success = success, output = output, messages = updates, error = error }), new System.Text.UTF8Encoding(false));
            else Console.WriteLine(success ? string.Join(Environment.NewLine, updates) : error);
        }
    }

    internal sealed class MainForm : Form
    {
        readonly TextBox input = new TextBox(), output = new TextBox(), audio = new TextBox();
        readonly TextBox browser = new TextBox(), ffmpeg = new TextBox(), stage = new TextBox(), start = new TextBox();
        readonly NumericUpDown width = Number(1280, 16, 7680), height = Number(720, 16, 7680);
        readonly NumericUpDown fps = Number(24, 1, 120), duration = Number(0, 0, 3600), crf = Number(18, 0, 51);
        readonly CheckBox fit = new CheckBox { Text = "舞台等比适配", Checked = true, AutoSize = true };
        readonly CheckBox hide = new CheckBox { Text = "隐藏舞台外控件", Checked = true, AutoSize = true };
        readonly CheckBox mute = new CheckBox { Text = "无音轨", AutoSize = true };
        readonly CheckBox fast = new CheckBox { Text = "快速模式", Checked = true, AutoSize = true };
        readonly Button run = new Button { Text = "开始转换", AutoSize = true };
        readonly Button cancel = new Button { Text = "取消", AutoSize = true, Enabled = false };
        readonly Button open = new Button { Text = "打开视频", AutoSize = true, Enabled = false };
        readonly Button folder = new Button { Text = "打开目录", AutoSize = true, Enabled = false };
        readonly ProgressBar bar = new ProgressBar { Dock = DockStyle.Fill };
        readonly Label status = new Label { Text = "就绪", AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        readonly TextBox log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
        readonly Panel settings = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        readonly ToolTip tips = new ToolTip();
        CancellationTokenSource cancellation;
        string lastOutput;
        bool closeRequested;

        internal MainForm(string initial)
        {
            Text = "HTML 转视频 · 精简版 " + Program.APP_VERSION;
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.White;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(830, 760);
            MinimumSize = new Size(760, 730);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = SystemIcons.Application;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 6 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 418));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var heading = new Label { Text = "HTML 转视频", AutoSize = true, Font = new Font(Font.FontFamily, 17F, FontStyle.Bold) };
            root.Controls.Add(heading, 0, 0);
            root.Controls.Add(settings, 0, 1);
            var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(0, 8, 0, 0) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
            settings.Controls.Add(table);
            AddPath(table, "HTML 文件", input, () => {
                string file = Pick("HTML 文件|*.html;*.htm|所有文件|*.*");
                if (file != null) SetInput(file);
            });
            AddPath(table, "输出 MP4", output, () => {
                using (var dialog = new SaveFileDialog { Filter = "MP4 视频|*.mp4", FileName = output.Text })
                    if (dialog.ShowDialog(this) == DialogResult.OK) output.Text = dialog.FileName;
            });
            AddPath(table, "音频文件", audio, () => {
                string file = Pick("音频 / 视频|*.mp3;*.m4a;*.wav;*.aac;*.ogg;*.mp4|所有文件|*.*");
                if (file != null) audio.Text = file;
            });
            AddPath(table, "浏览器", browser, () => {
                string file = Pick("浏览器程序|msedge.exe;chrome.exe|EXE|*.exe");
                if (file != null) browser.Text = file;
            });
            AddPath(table, "FFmpeg", ffmpeg, () => {
                string file = Pick("FFmpeg|ffmpeg.exe|EXE|*.exe");
                if (file != null) ffmpeg.Text = file;
            });
            var sizeRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            var presets = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
            presets.Items.AddRange(new object[] { "1920 x 1080", "1280 x 720", "2560 x 1440", "1080 x 1920", "720 x 1280", "自定义" });
            presets.SelectedIndex = 1;
            bool selectingPreset = false;
            presets.SelectedIndexChanged += (s, e) => {
                if (presets.SelectedIndex == 5) return;
                string[] parts = presets.Text.Split('x');
                selectingPreset = true;
                width.Value = int.Parse(parts[0]); height.Value = int.Parse(parts[1]);
                selectingPreset = false;
            };
            width.ValueChanged += (s, e) => { if (!selectingPreset) presets.SelectedIndex = 5; };
            height.ValueChanged += (s, e) => { if (!selectingPreset) presets.SelectedIndex = 5; };
            sizeRow.Controls.AddRange(new Control[] { presets, width, new Label { Text = "×", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, height });
            AddRow(table, "分辨率", sizeRow, null);
            var timing = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            duration.DecimalPlaces = 2; duration.Increment = 0.1M;
            timing.Controls.AddRange(new Control[] { fps, Inline("fps"), duration, Inline("秒（0 = 自动）"), crf, Inline("CRF") });
            AddRow(table, "视频参数", timing, null);
            AddRow(table, "舞台选择器", stage, null);
            AddRow(table, "播放选择器", start, null);
            var toggles = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            toggles.Controls.AddRange(new Control[] { fit, hide, mute, fast });
            AddRow(table, "", toggles, null);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            run.Padding = new Padding(14, 2, 14, 2);
            run.BackColor = Color.FromArgb(28, 118, 87); run.ForeColor = Color.White;
            actions.Controls.AddRange(new Control[] { run, cancel, open, folder });
            root.Controls.Add(actions, 0, 2);
            root.Controls.Add(bar, 0, 3);
            root.Controls.Add(status, 0, 4);
            root.Controls.Add(log, 0, 5);
            Controls.Add(root);
            tips.SetToolTip(stage, "CSS 选择器，例如 #stage；留空自动识别。");
            tips.SetToolTip(start, "CSS 选择器，例如 #startBtn；留空识别播放按钮。");
            tips.SetToolTip(audio, "留空使用 HTML 中的本地或内嵌音轨；短音轨循环到视频结束。");
            tips.SetToolTip(crf, "数值越小质量越高、文件越大。");
            tips.SetToolTip(fast, "JPEG 95 截图 + veryfast 编码，有轻微有损压缩；关闭后使用 PNG + medium。");
            tips.SetToolTip(width, "宽高必须是偶数。");
            tips.SetToolTip(input, "支持拖入 HTML 文件。");
            browser.Text = Engine.Browser(); ffmpeg.Text = Engine.Ffmpeg();
            AllowDrop = true;
            DragEnter += (s, e) => { if (cancellation == null && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += (s, e) => {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files.Length > 0) SetInput(files[0]);
            };
            run.Click += async (s, e) => await Run();
            cancel.Click += (s, e) => { cancellation.Cancel(); cancel.Enabled = false; status.Text = "正在取消…"; };
            open.Click += (s, e) => Shell(lastOutput);
            folder.Click += (s, e) => Shell(Path.GetDirectoryName(lastOutput));
            FormClosing += (s, e) => {
                if (cancellation == null) return;
                e.Cancel = true;
                if (MessageBox.Show(this, "取消当前转换并退出？", "正在转换", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                { closeRequested = true; cancellation.Cancel(); }
            };
            if (!string.IsNullOrWhiteSpace(initial)) SetInput(initial);
        }

        static NumericUpDown Number(decimal value, decimal min, decimal max)
        { return new NumericUpDown { Minimum = min, Maximum = max, Value = value, Width = 83, ThousandsSeparator = false }; }
        static Label Inline(string text)
        { return new Label { Text = text, AutoSize = true, Padding = new Padding(0, 5, 8, 0) }; }
        static void AddRow(TableLayoutPanel table, string label, Control field, Control action)
        {
            int row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            table.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            field.Dock = DockStyle.Fill;
            field.Margin = new Padding(3, 5, 3, 5);
            table.Controls.Add(field, 1, row);
            if (action != null) { action.Dock = DockStyle.Fill; action.Margin = new Padding(6, 4, 0, 4); table.Controls.Add(action, 2, row); }
            else table.SetColumnSpan(field, 2);
        }
        static void AddPath(TableLayoutPanel table, string label, TextBox field, Action pick)
        {
            var button = new Button { Text = "选择…" };
            button.Click += (s, e) => pick();
            AddRow(table, label, field, button);
        }
        string Pick(string filter)
        {
            using (var dialog = new OpenFileDialog { Filter = filter })
                return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
        }
        void SetInput(string file)
        {
            input.Text = file;
            output.Text = Path.ChangeExtension(file, ".mp4");
        }
        void Shell(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception e) { MessageBox.Show(this, e.Message, "无法打开", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
        async Task Run()
        {
            var o = new Options {
                Input = input.Text.Trim(), Output = output.Text.Trim(), Audio = audio.Text.Trim(),
                Browser = browser.Text.Trim(), Ffmpeg = ffmpeg.Text.Trim(), Stage = stage.Text.Trim(), Start = start.Text.Trim(),
                Width = (int)width.Value, Height = (int)height.Value, Fps = (int)fps.Value,
                Duration = (double)duration.Value, Crf = (int)crf.Value, Fit = fit.Checked, Hide = hide.Checked, Mute = mute.Checked, Fast = fast.Checked
            };
            if (File.Exists(o.Output))
            {
                if (MessageBox.Show(this, "输出文件已存在，是否覆盖？", "确认覆盖", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                o.Overwrite = true;
            }
            log.Clear(); bar.Value = 0;
            cancellation = new CancellationTokenSource();
            settings.Enabled = run.Enabled = open.Enabled = folder.Enabled = false;
            cancel.Enabled = true;
            string previous = "";
            var progress = new Progress<Update>(u => {
                bar.Value = Math.Max(0, Math.Min(100, u.Percent)); status.Text = u.Message.Replace("\r\n", " ");
                if (!u.Message.StartsWith("渲染 ") && u.Message != previous)
                    log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + u.Message + Environment.NewLine);
                previous = u.Message;
            });
            try
            {
                lastOutput = await Task.Run(() => Engine.ConvertVideo(o, u => ((IProgress<Update>)progress).Report(u), cancellation.Token));
                open.Enabled = folder.Enabled = true;
            }
            catch (OperationCanceledException)
            { status.Text = "已取消，未修改原有输出文件"; log.AppendText(status.Text + Environment.NewLine); }
            catch (Exception e)
            {
                status.Text = "转换失败"; log.AppendText(e.Message + Environment.NewLine);
                MessageBox.Show(this, e.Message, "转换失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                cancellation.Dispose(); cancellation = null;
                settings.Enabled = run.Enabled = true; cancel.Enabled = false;
                if (closeRequested) Close();
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) tips.Dispose();
            base.Dispose(disposing);
        }
    }
}
