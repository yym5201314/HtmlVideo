using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace HtmlVideoLite
{
    // Simulates Chromium launchers that hand over to a child, or fail to start.
    internal static class BrowserLauncher
    {
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--fixture-child")
            {
                Thread.Sleep(1000);
                string executable = Environment.GetEnvironmentVariable("HVL_TEST_BROWSER");
                using (Process browser = Process.Start(Engine.StartInfo(executable,
                    args.Skip(1).Concat(new[] { "--edge-skip-compat-layer-relaunch" }))))
                    browser.WaitForExit();
                return 0;
            }
            string mode = Environment.GetEnvironmentVariable("HVL_TEST_LAUNCH_MODE");
            if (mode == "idle") { Thread.Sleep(30000); return 0; }
            if (mode == "fail" || (mode == "fallback" && !args.Contains("--disable-gpu")))
            {
                Console.Error.WriteLine("fixture browser startup failure");
                return 88;
            }
            var info = Engine.StartInfo(Assembly.GetExecutingAssembly().Location,
                new[] { "--fixture-child" }.Concat(args));
            using (Process child = Process.Start(info))
            {
                // Give the parent time to assign this launcher to its Windows Job.
                Thread.Sleep(200);
            }
            return 0;
        }
    }
}
