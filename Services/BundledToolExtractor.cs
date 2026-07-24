using System.IO;
using System.Reflection;

namespace SystemPulse.Services;

internal static class BundledToolExtractor
{
    private static readonly string CacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SystemPulse",
        "v.06",
        "Bundled");

    public static string Resolve(string relativePath, string resourceName)
    {
        var besideApplication = Path.Combine(AppContext.BaseDirectory, relativePath);
        if (File.Exists(besideApplication))
            return besideApplication;

        var target = Path.Combine(CacheRoot, relativePath);
        try
        {
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (resource is null)
                return target;

            if (File.Exists(target) && new FileInfo(target).Length == resource.Length)
                return target;

            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var temporary = target + ".tmp-" + Environment.ProcessId;
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                resource.CopyTo(output);

            File.Move(temporary, target, overwrite: true);
        }
        catch
        {
            // Callers already handle a missing optional bundled tool gracefully.
        }

        return target;
    }
}
