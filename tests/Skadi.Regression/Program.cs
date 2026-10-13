using EventCapture.App.Services;
using System.IO;
using System.Text.Json;
using System.Windows;

internal static class Program
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }

    [STAThread]
    private static void Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "Skadi-Regression-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "settings.json");
            AtomicSettingsFile.Write(path, "{\"BufferEnabled\":true}");
            AtomicSettingsFile.Write(path, "{\"BufferEnabled\":false}");
            Check(File.ReadAllText(path).Contains("false"), "Settings update committed");
            Check(File.ReadAllText(path + ".bak").Contains("true"), "Previous settings retained in backup");
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { AtomicSettingsFile.Write(path, "{}"); throw new Exception("Expected locked-file error"); }
                catch (IOException) { }
            }
            Check(File.ReadAllText(path).Contains("false"), "Failed replacement preserves original");
            Check(Directory.GetFiles(root, "*.tmp").Length == 0, "Failed replacement cleans temporary file");
            Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() =>
                AtomicSettingsFile.Write(path, JsonSerializer.Serialize(new { Value = i }))))).GetAwaiter().GetResult();
            using var current = JsonDocument.Parse(File.ReadAllText(path));
            using var backup = JsonDocument.Parse(File.ReadAllText(path + ".bak"));
            Check(current.RootElement.TryGetProperty("Value", out _), "Concurrent writes commit complete JSON");
            TestRetries().GetAwaiter().GetResult();

            Environment.SetEnvironmentVariable("SKADI_SETTINGS_PATH", path);
            var settings = new EventCapture.App.AppSettings { BufferEnabled = true };
            settings.Save();
            settings.Save();
            Check(EventCapture.App.AppSettings.Load().BufferEnabled, "Replay preference survives settings round trip");
            File.WriteAllText(path, "{broken");
            Check(EventCapture.App.AppSettings.Load().BufferEnabled, "Corrupt settings recover replay preference from backup");
            if (args.Contains("--capture"))
            {
                Task.Run(async () =>
                {
                    await using var capture = new CaptureCoordinator();
                    var replaySettings = new EventCapture.App.AppSettings
                    {
                        BufferEnabled = true, CaptureMode = "Video", Resolution = "1280x720",
                        Fps = 30, BufferSeconds = 15, SaveFolder = root
                    };
                    await capture.ApplySettingsAsync(replaySettings, restartPipeline: true);
                    await Task.Delay(2500);
                    Check(capture.CapturedFrames > 0, "Real GPU replay pipeline receives frames");
                    await capture.StopAllAsync();
                }).GetAwaiter().GetResult();
            }

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Skadi;component/Resources/Colors.xaml")
            });
            using var tray = new TrayIconService(Path.Combine(AppContext.BaseDirectory, "EventCapture.ico"));
            tray.UpdateHotkeys("Alt+F1", "Alt+F3");
            var menu = (System.Windows.Controls.ContextMenu)typeof(TrayIconService)
                .GetField("_menu", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(tray)!;
            menu.IsOpen = true;
            app.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check(menu.IsOpen && menu.ActualWidth > 0, "WPF tray menu opens and lays out");
            menu.IsOpen = false;
            tray.Dispose();
            tray.Dispose();
            Check(true, "Native tray with WPF resources creates, updates and disposes twice");
            app.Shutdown();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task TestRetries()
    {
        int attempts = 0;
        var waits = new List<TimeSpan>();
        await StartupReplayRecovery.RunAsync(() =>
        {
            if (++attempts < 3) throw new InvalidOperationException("Device not ready");
            return Task.CompletedTask;
        }, () => true, (_, _) => { }, CancellationToken.None,
            (delay, _) => { waits.Add(delay); return Task.CompletedTask; });
        Check(attempts == 3 && waits.SequenceEqual(new[] { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6) }),
            "Transient startup failure recovers with backoff");
        attempts = 0;
        try
        {
            await StartupReplayRecovery.RunAsync(() => { attempts++; throw new InvalidOperationException(); },
                () => true, (_, _) => { }, CancellationToken.None, (_, _) => Task.CompletedTask);
            throw new Exception("Expected exhausted retries");
        }
        catch (InvalidOperationException) { }
        Check(attempts == 4, "Persistent failure stops after four attempts");
        attempts = 0;
        try
        {
            await StartupReplayRecovery.RunAsync(() => { attempts++; throw new InvalidOperationException(); },
                () => false, (_, _) => { }, CancellationToken.None, (_, _) => Task.CompletedTask);
        }
        catch (InvalidOperationException) { }
        Check(attempts == 1, "Disabled replay does not retry");
        using var cts = new CancellationTokenSource();
        attempts = 0;
        try
        {
            await StartupReplayRecovery.RunAsync(() => { attempts++; throw new InvalidOperationException(); },
                () => true, (_, _) => cts.Cancel(), cts.Token, (_, token) => Task.Delay(1, token));
        }
        catch (OperationCanceledException) { }
        Check(attempts == 1, "Shutdown or manual override cancels retry");
    }
}
