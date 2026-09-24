using System.Text.RegularExpressions;

namespace ResilientRateLimiting.Tests;

internal static partial class DocumentationSnippets
{
    private const string FilePrefix = "file:";

    [GeneratedRegex(@"^//\s*snippet:\s*([a-z0-9-]+)\s*$")]
    private static partial Regex RegionStart();

    [GeneratedRegex(@"^<!--\s*snippet:\s*(\S+)\s*-->$")]
    private static partial Regex DocMarker();

    public static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ResilientRateLimiting.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    public static IReadOnlyList<string> FindErrors(string repoRoot)
    {
        var regions = new Dictionary<string, string>();
        var errors = new List<string>();
        var samples = Path.Combine(repoRoot, "samples");

        if (Directory.Exists(samples))
        {
            foreach (var file in Directory.EnumerateFiles(samples, "*.cs", SearchOption.AllDirectories).Where(IsSource))
            {
                foreach (var (name, content) in ReadRegions(File.ReadAllText(file), file))
                {
                    if (!regions.TryAdd(name, content))
                    {
                        errors.Add($"Snippet '{name}' is defined twice (second in {file}).");
                    }
                }
            }

            foreach (var file in Directory.EnumerateFiles(samples, "*.json", SearchOption.AllDirectories).Where(IsSource))
            {
                var relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                regions[FilePrefix + relative] = Normalise(File.ReadAllText(file).Split('\n'));
            }
        }

        var documents = new List<string>();
        var readme = Path.Combine(repoRoot, "README.md");

        if (File.Exists(readme))
        {
            documents.Add(readme);
        }

        var folder = Path.Combine(repoRoot, "documentation");

        if (Directory.Exists(folder))
        {
            documents.AddRange(Directory.EnumerateFiles(folder, "*.md", SearchOption.AllDirectories));
        }

        foreach (var document in documents)
        {
            errors.AddRange(CheckDocument(File.ReadAllText(document), Path.GetRelativePath(repoRoot, document), regions));
        }

        return errors;
    }

    public static Dictionary<string, string> ReadRegions(string source, string path)
    {
        var lines = source.Split('\n');
        var regions = new Dictionary<string, string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var match = RegionStart().Match(lines[i].Trim());

            if (!match.Success)
            {
                continue;
            }

            var name = match.Groups[1].Value;
            var end = Array.FindIndex(lines, i + 1, line => line.Trim() == "// end-snippet");

            if (end < 0)
            {
                throw new InvalidOperationException($"Snippet '{name}' in {path} has no '// end-snippet'.");
            }

            if (!regions.TryAdd(name, Normalise(lines[(i + 1)..end])))
            {
                throw new InvalidOperationException($"Snippet '{name}' is defined twice in {path}.");
            }

            i = end;
        }

        return regions;
    }

    public static List<string> CheckDocument(string document, string path, IReadOnlyDictionary<string, string> regions)
    {
        var lines = document.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var errors = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var fence = lines[i].Trim();

            if (fence is not ("```csharp" or "```json"))
            {
                continue;
            }

            var close = Array.FindIndex(lines, i + 1, line => line.Trim() == "```");

            if (close < 0)
            {
                errors.Add($"{path}:{i + 1}: code block is never closed.");
                break;
            }

            var block = Normalise(lines[(i + 1)..close]);
            var markerLine = i - 1;

            while (markerLine >= 0 && lines[markerLine].Trim().Length == 0)
            {
                markerLine--;
            }

            var marker = markerLine >= 0 ? DocMarker().Match(lines[markerLine].Trim()) : Match.Empty;

            if (!marker.Success)
            {
                errors.Add($"{path}:{i + 1}: code block has no '<!-- snippet: name -->' marker above it.");
            }
            else if (!regions.TryGetValue(marker.Groups[1].Value, out var expected))
            {
                errors.Add($"{path}:{i + 1}: snippet '{marker.Groups[1].Value}' is not defined in any sample.");
            }
            else if (expected != block)
            {
                errors.Add($"{path}:{i + 1}: snippet '{marker.Groups[1].Value}' differs from its sample.");
            }

            i = close;
        }

        return errors;
    }

    private static bool IsSource(string file) =>
        !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
        && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}");

    private static string Normalise(string[] lines)
    {
        var trimmed = lines.Select(line => line.TrimEnd('\r').TrimEnd()).ToList();

        while (trimmed.Count > 0 && trimmed[0].Length == 0)
        {
            trimmed.RemoveAt(0);
        }

        while (trimmed.Count > 0 && trimmed[^1].Length == 0)
        {
            trimmed.RemoveAt(trimmed.Count - 1);
        }

        var indent = trimmed.Where(line => line.Length > 0).Select(line => line.Length - line.TrimStart().Length).DefaultIfEmpty(0).Min();

        return string.Join("\n", trimmed.Select(line => line.Length >= indent ? line[indent..] : line));
    }
}
