namespace Grab.Services;

/// <summary>Cleans up the small player copies Grab makes while you work.</summary>
public static class TempFiles
{
    /// <summary>Deletes every preview copy. Files still in use are skipped and removed next time.</summary>
    public static void CleanPreviews()
    {
        try
        {
            if (!Directory.Exists(AppPaths.TempDir)) return;
            foreach (var dir in Directory.GetDirectories(AppPaths.TempDir, "preview-*"))
            {
                try { Directory.Delete(dir, true); } catch { }
            }
            foreach (var file in Directory.GetFiles(AppPaths.TempDir, "preview-*"))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch
        {
            // best effort
        }
    }

    public static long PreviewBytes()
    {
        long total = 0;
        try
        {
            if (!Directory.Exists(AppPaths.TempDir)) return 0;
            foreach (var dir in Directory.GetDirectories(AppPaths.TempDir, "preview-*"))
                foreach (var f in new DirectoryInfo(dir).GetFiles("*", SearchOption.AllDirectories))
                    total += f.Length;
            foreach (var f in new DirectoryInfo(AppPaths.TempDir).GetFiles("preview-*"))
                total += f.Length;
        }
        catch
        {
            // best effort
        }
        return total;
    }
}

public static class AppLog
{
    public static string PathFor(string name) => Path.Combine(AppPaths.DataDir, name);

    public static void Write(string name, string text)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(PathFor(name), text ?? "");
        }
        catch
        {
            // not critical
        }
    }
}
