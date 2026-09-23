using System.IO.Compression;
using System.Xml.Linq;

namespace DotnetDeployer.Fleet.WorkerService.Execution;

public static class NuGetPackageReader
{
    public static string ReadPackageId(string nupkgPath)
    {
        using var stream = File.OpenRead(nupkgPath);
        return ReadPackageId(stream, Path.GetFileName(nupkgPath));
    }

    public static string ReadPackageId(Stream stream, string fileName = "package.nupkg")
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var nuspecEntry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No .nuspec file found inside '{fileName}'.");

        using var entryStream = nuspecEntry.Open();
        var doc = XDocument.Load(entryStream);
        var idElement = doc.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, "id", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No <id> element found in .nuspec inside '{fileName}'.");

        var id = idElement.Value?.Trim();
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException($"Empty <id> element in .nuspec inside '{fileName}'.");

        return id;
    }
}
