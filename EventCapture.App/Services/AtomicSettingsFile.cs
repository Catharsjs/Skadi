using System.IO;
using System.Text;

namespace EventCapture.App.Services;

internal static class AtomicSettingsFile
{
    private static readonly object Sync = new();
    public static void Write(string path, string json)
    {
        lock (Sync)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                // Same-directory replacement commits a complete file, retaining the previous version.
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
