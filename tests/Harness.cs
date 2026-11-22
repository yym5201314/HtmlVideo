using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace HtmlVideoLite
{
    internal static class Harness
    {
        [STAThread]
        static int Main(string[] args)
        {
            string root = Path.GetFullPath(args[0]);
            string results = Path.Combine(root, "build", "smoke");
            Directory.CreateDirectory(results);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                using (var form = new MainForm(Path.Combine(root, "tests", "animation.html")))
                {
                    form.Show();
                    Application.DoEvents();
                    AssertBounds(form);
                    using (var bitmap = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                        bitmap.Save(Path.Combine(results, "window.png"));
                    }
                    form.Size = form.MinimumSize;
                    Application.DoEvents();
                    AssertBounds(form);
                    using (var bitmap = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                        bitmap.Save(Path.Combine(results, "window-minimum.png"));
                    }
                    string guiOutput = Path.Combine(results, "gui-conversion.mp4");
                    if (File.Exists(guiOutput)) File.Delete(guiOutput);
                    Field<TextBox>(form, "output").Text = guiOutput;
                    Field<NumericUpDown>(form, "width").Value = 640;
                    Field<NumericUpDown>(form, "height").Value = 360;
                    Field<NumericUpDown>(form, "fps").Value = 10;
                    Field<TextBox>(form, "ffmpeg").Text = Path.Combine(root, "tools", "ffmpeg.exe");
                    Field<Button>(form, "run").PerformClick();
                    var watch = Stopwatch.StartNew();
                    while (!Field<Button>(form, "run").Enabled && watch.ElapsedMilliseconds < 60000)
                    {
                        Application.DoEvents();
                        Thread.Sleep(20);
                    }
                    if (!File.Exists(guiOutput) || !Field<Button>(form, "open").Enabled)
                        throw new Exception("GUI conversion failed: " + Field<TextBox>(form, "log").Text);
                    while (Field<Button>(form, "cancel").Enabled && watch.ElapsedMilliseconds < 65000)
                    { Application.DoEvents(); Thread.Sleep(20); }
                    if (!Field<Panel>(form, "settings").Enabled || Field<Button>(form, "cancel").Enabled)
                        throw new Exception("GUI did not return to ready state");
                    Application.DoEvents();
                    form.Refresh();
                    using (var bitmap = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                        bitmap.Save(Path.Combine(results, "window-completed.png"));
                    }
                    form.Close();
                }
                Console.WriteLine("GUI layout and button-to-MP4 workflow PASS");
                string output = Path.Combine(results, "preserve-on-cancel.mp4");
                File.WriteAllText(output, "original-output-must-survive");
                var before = new HashSet<string>(Directory.GetDirectories(Path.GetTempPath(), "HtmlVideoLite-*"));
                using (var cancellation = new CancellationTokenSource())
                {
                    var options = new Options {
                        Input = Path.Combine(root, "tests", "animation.html"), Output = output,
                        Ffmpeg = Path.Combine(root, "tools", "ffmpeg.exe"), Duration = 10,
                        Width = 320, Height = 180, Fps = 10, Overwrite = true
                    };
                    try
                    {
                        Engine.ConvertVideo(options, u => {
                            if (u.Message.StartsWith("渲染 ")) cancellation.Cancel();
                        }, cancellation.Token).GetAwaiter().GetResult();
                        throw new Exception("Cancellation unexpectedly succeeded");
                    }
                    catch (OperationCanceledException) { }
                }
                if (File.ReadAllText(output) != "original-output-must-survive") throw new Exception("Existing output modified on cancellation");
                var leftover = Directory.GetDirectories(Path.GetTempPath(), "HtmlVideoLite-*").Where(p => !before.Contains(p)).ToArray();
                if (leftover.Length > 0) throw new Exception("Temporary directory leaked: " + string.Join(", ", leftover));
                Console.WriteLine("Cancellation, output preservation and private temp cleanup PASS");
                BrowserStartupChecks(root, results);
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }

        static void AssertBounds(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                if (!child.Visible) continue;
                if (child.Left < -1 || child.Top < -1 || child.Right > parent.ClientSize.Width + 1 ||
                    child.Bottom > parent.ClientSize.Height + 1)
                    throw new Exception("Control outside parent: " + child.GetType().Name + " " + child.Text);
                AssertBounds(child);
            }
        }
        static T Field<T>(object target, string name) where T : class
        {
            return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        }

        static void BrowserStartupChecks(string root, string results)
        {
            string previousMode = Environment.GetEnvironmentVariable("HVL_TEST_LAUNCH_MODE");
            string previousBrowser = Environment.GetEnvironmentVariable("HVL_TEST_BROWSER");
            Environment.SetEnvironmentVariable("HVL_TEST_BROWSER", Engine.Browser());
            try
            {
                foreach (string mode in new[] { "forward", "fallback", "fail", "idle" })
                {
                    Environment.SetEnvironmentVariable("HVL_TEST_LAUNCH_MODE", mode);
                    var messages = new List<string>();
                    var options = new Options {
                        Input = Path.Combine(root, "tests", "animation.html"),
                        Output = Path.Combine(results, "browser-" + mode + ".mp4"),
                        Browser = Path.Combine(root, "build", "BrowserLauncher.exe"),
                        Ffmpeg = Path.Combine(root, "tools", "ffmpeg.exe"),
                        Duration = 0.3, Width = 320, Height = 180, Fps = 10, Overwrite = true
                    };
                    using (var cancellation = new CancellationTokenSource())
                    {
                        if (mode == "idle") cancellation.CancelAfter(500);
                        try
                        {
                            Engine.ConvertVideo(options, u => messages.Add(u.Message), cancellation.Token).GetAwaiter().GetResult();
                            if (mode == "fail" || mode == "idle") throw new Exception("Failed/cancelled startup unexpectedly succeeded");
                            if (mode == "fallback" && !messages.Any(m => m.Contains("软件渲染")))
                                throw new Exception("Software-rendering retry did not run");
                        }
                        catch (IOException e)
                        {
                            if (mode != "fail") throw;
                            if (!e.Message.Contains("88") || !e.Message.Contains("fixture browser startup failure") ||
                                !e.Message.Contains("last-browser-startup.log"))
                                throw new Exception("Startup diagnostic missing exit code, stderr or log path", e);
                        }
                        catch (OperationCanceledException) { if (mode != "idle") throw; }
                    }
                    Console.WriteLine("Browser startup " + mode + " PASS");
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("HVL_TEST_LAUNCH_MODE", previousMode);
                Environment.SetEnvironmentVariable("HVL_TEST_BROWSER", previousBrowser);
            }
        }
    }
}
