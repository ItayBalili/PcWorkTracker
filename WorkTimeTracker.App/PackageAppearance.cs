using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WorkTimeTracker.App;

internal sealed record PackageAppearance(string? Name, ImageSource? Icon)
{
    private static readonly PackageAppearance Empty = new(null, null);
    private static readonly ConcurrentDictionary<string, PackageAppearance> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static PackageAppearance ForExecutable(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) ||
            executable.StartsWith(@"\\", StringComparison.Ordinal)) return Empty;
        return Cache.GetOrAdd(executable, Load);
    }

    private static PackageAppearance Load(string executable)
    {
        try
        {
            var windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps") + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(executable);
            if (!full.StartsWith(windowsApps, StringComparison.OrdinalIgnoreCase)) return Empty;
            var relative = Path.GetRelativePath(windowsApps, full);
            var separator = relative.IndexOf(Path.DirectorySeparatorChar);
            if (separator <= 0) return Empty;
            var packageName = relative[..separator];
            var packageDirectory = Path.Combine(windowsApps, packageName);
            var appPath = relative[(separator + 1)..];
            return ReadPackage(packageDirectory, packageName, appPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException or ArgumentException or
                                      Win32Exception or NotSupportedException)
        {
            DashboardLog.Write(error);
            return Empty;
        }
    }

    private static PackageAppearance ReadPackage(string packageDirectory, string packageName, string appPath)
    {
        using var reader = XmlReader.Create(Path.Combine(packageDirectory, "AppxManifest.xml"),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var manifest = XDocument.Load(reader);
        var application = manifest.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "Application" &&
            string.Equals((string?)element.Attribute("Executable"), appPath, StringComparison.OrdinalIgnoreCase));
        var visual = application?.Elements().FirstOrDefault(element => element.Name.LocalName == "VisualElements");
        var display = (string?)visual?.Attribute("DisplayName");
        if (display?.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) == true)
        {
            var buffer = new StringBuilder(1024);
            var result = SHLoadIndirectString($"@{{{packageName}?{display}}}", buffer, (uint)buffer.Capacity, 0);
            display = result == 0 ? buffer.ToString() : null;
        }
        var logo = (string?)visual?.Attribute("Square44x44Logo");
        return new PackageAppearance(string.IsNullOrWhiteSpace(display) ? null : display, LoadLogo(packageDirectory, logo));
    }

    private static ImageSource? LoadLogo(string packageDirectory, string? logo)
    {
        if (string.IsNullOrWhiteSpace(logo)) return null;
        var path = Path.GetFullPath(Path.Combine(packageDirectory, logo));
        if (!path.StartsWith(packageDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        var folder = Path.GetDirectoryName(path);
        if (folder is null || !Directory.Exists(folder)) return null;
        var stem = Path.GetFileNameWithoutExtension(path);
        var candidates = Directory.GetFiles(folder, stem + "*.png");
        var file = candidates.OrderBy(candidate =>
            candidate.Contains(".targetsize-16", StringComparison.OrdinalIgnoreCase) ? 0 :
            candidate.Contains(".scale-100", StringComparison.OrdinalIgnoreCase) ? 1 :
            string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase) ? 2 : 3).FirstOrDefault();
        if (file is null) return null;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 32;
        image.UriSource = new Uri(file);
        image.EndInit();
        image.Freeze();
        return image;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder output, uint outputSize, nint reserved);
}
