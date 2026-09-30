using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using TiaMcpServer.Contracts;

namespace TiaMcpServer.OpennessWorker.Openness;

/// <summary>
/// ANet fork: exports PLC blocks, PLC data types and tag tables of one PLC into a folder under a
/// configured export root, so a client can read large objects from disk instead of through the
/// 60,000-character response limit. The project itself is never modified.
///
/// <para>
/// The work is chunked by a time budget: one call exports items in deterministic order starting at
/// <c>offset</c> until the budget is spent and reports <c>nextOffset</c>. The client repeats the
/// call with the returned <c>runId</c> and <c>nextOffset</c> until <c>complete</c> is true. Every
/// exported item is appended as one JSON line to <c>manifest.jsonl</c> in the run folder.
/// </para>
/// </summary>
internal static class ProjectFolderExporter
{
    internal const string KindBlock = "block";
    internal const string KindType = "type";
    internal const string KindTagTable = "tagTable";

    internal const string IncludeBlocks = "blocks";
    internal const string IncludeTypes = "types";
    internal const string IncludeTagTables = "tagTables";

    internal const string FormatXml = "xml";
    internal const string FormatDocuments = "documents";
    internal const string FormatSource = "source";

    private static readonly Regex RunIdPattern = new("^[0-9]{8}-[0-9]{6}$", RegexOptions.CultureInvariant);
    private static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(35);
    private const int MaxReportedFailures = 25;
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static ProjectFolderExportResult Export(Project project, WorkerRequest request)
    {
        var exportRoot = ValidateExportRoot(request.ExportRoot);
        var include = NormalizeSet(
            request.ExportInclude,
            new[] { IncludeBlocks, IncludeTypes, IncludeTagTables },
            "include");
        var formats = NormalizeSet(
            request.ExportFormats,
            new[] { FormatXml, FormatDocuments, FormatSource },
            "formats",
            defaults: new[] { FormatDocuments, FormatSource });
        var offset = request.ExportOffset ?? 0;
        if (offset < 0)
        {
            throw new WorkerOperationException(WorkerFailureCategories.ValidationError, "offset must be 0 or greater.");
        }

        var runId = request.ExportRunId;
        if (runId is null)
        {
            if (offset != 0)
            {
                throw new WorkerOperationException(
                    WorkerFailureCategories.ValidationError,
                    "offset greater than 0 requires the runId returned by the first call.");
            }

            runId = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        }
        else if (!RunIdPattern.IsMatch(runId))
        {
            throw new WorkerOperationException(
                WorkerFailureCategories.ValidationError,
                "runId must be the value returned by a previous export_to_folder call (yyyyMMdd-HHmmss).");
        }

        var plcSoftware = PlcSoftwareLocator.Find(project, request.PlcName);
        var plcFolderName = SafeName(plcSoftware.Name);
        var projectFolderName = SafeName(project.Name);
        var runDirectory = Path.Combine(exportRoot, projectFolderName, runId);
        var plcDirectory = Path.Combine(runDirectory, plcFolderName);
        EnsureUnderRoot(exportRoot, plcDirectory);
        Directory.CreateDirectory(plcDirectory);

        var items = Enumerate(plcSoftware, include)
            .Where(item => MatchesPathFilter(item.Path, request.ExportPathPrefixes))
            .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (offset > items.Count)
        {
            throw new WorkerOperationException(
                WorkerFailureCategories.ValidationError,
                $"offset {offset} is beyond the {items.Count} exportable items.");
        }

        var budget = request.ExportTimeBudgetSeconds is > 0 and <= 240
            ? TimeSpan.FromSeconds(request.ExportTimeBudgetSeconds.Value)
            : DefaultBudget;
        var stopwatch = Stopwatch.StartNew();
        var manifestPath = Path.Combine(runDirectory, "manifest.jsonl");
        var result = new ProjectFolderExportResult
        {
            ExportFolder = runDirectory,
            PlcFolder = plcDirectory,
            ManifestPath = manifestPath,
            RunId = runId,
            PlcName = plcSoftware.Name,
            TotalItems = items.Count,
            Offset = offset,
            Formats = formats.ToList(),
            Include = include.ToList(),
        };

        var index = offset;
        using (var manifest = new StreamWriter(manifestPath, append: true, new UTF8Encoding(false)))
        {
            while (index < items.Count)
            {
                if (index > offset && stopwatch.Elapsed >= budget)
                {
                    break;
                }

                var item = items[index];
                var entry = ExportItem(item, plcDirectory, plcSoftware, formats);
                manifest.WriteLine(JsonSerializer.Serialize(entry, ManifestJsonOptions));
                result.ProcessedItems++;
                if (entry.Error is null)
                {
                    result.SucceededItems++;
                }
                else
                {
                    result.FailedItems++;
                    if (result.Failures.Count < MaxReportedFailures)
                    {
                        result.Failures.Add(new ProjectFolderExportFailure { Path = entry.Path, Error = entry.Error });
                    }
                }

                result.FileCount += entry.Files.Count;
                result.TotalBytes += entry.Files.Sum(file => file.Bytes);
                index++;
            }
        }

        result.Complete = index >= items.Count;
        result.NextOffset = result.Complete ? null : index;
        result.ElapsedMs = stopwatch.ElapsedMilliseconds;
        return result;
    }

    private static ProjectFolderManifestEntry ExportItem(
        ExportItemDescriptor item,
        string plcDirectory,
        PlcSoftware plcSoftware,
        IReadOnlyCollection<string> formats)
    {
        var entry = new ProjectFolderManifestEntry { Kind = item.Kind, Path = item.Path };
        var directory = Path.Combine(new[] { plcDirectory }.Concat(item.FolderSegments.Select(SafeName)).ToArray());
        var baseName = SafeName(item.Name);
        var errors = new List<string>();

        try
        {
            EnsureUnderRoot(plcDirectory, directory);
            Directory.CreateDirectory(directory);

            switch (item.Kind)
            {
                case KindBlock:
                    ExportBlock((PlcBlock)item.Engineering, plcSoftware, plcDirectory, directory, baseName, formats, entry, errors);
                    break;
                case KindType:
                    ExportType((PlcType)item.Engineering, plcSoftware, plcDirectory, directory, baseName, formats, entry, errors);
                    break;
                case KindTagTable:
                    RunStep(errors, "xml", () =>
                    {
                        var file = new FileInfo(Path.Combine(directory, baseName + ".xml"));
                        DeleteIfExists(file);
                        ((PlcTagTable)item.Engineering).Export(file, ExportOptions.None);
                        AddFile(entry, plcDirectory, file.FullName);
                    });
                    break;
            }
        }
        catch (Exception exception)
        {
            errors.Add(exception.Message);
        }

        entry.Error = errors.Count == 0 ? null : string.Join(" | ", errors);
        return entry;
    }

    private static void ExportBlock(
        PlcBlock block,
        PlcSoftware plcSoftware,
        string plcDirectory,
        string directory,
        string baseName,
        IReadOnlyCollection<string> formats,
        ProjectFolderManifestEntry entry,
        List<string> errors)
    {
        entry.BlockKind = BlockKindName(block);
        entry.Language = SafeLanguage(block);
        entry.Number = SafeNumber(block);

        var documentsOk = false;
        if (formats.Contains(FormatDocuments))
        {
            documentsOk = RunStep(errors, "documents", () =>
            {
                foreach (var stale in Directory.GetFiles(directory, baseName + ".*"))
                {
                    if (stale.EndsWith(".s7dcl", StringComparison.OrdinalIgnoreCase) ||
                        stale.EndsWith(".s7res", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(stale);
                    }
                }

                var result = block.ExportAsDocuments(new DirectoryInfo(directory), baseName);
                if (result.State != DocumentResultState.Success)
                {
                    throw new InvalidOperationException($"document export state '{result.State}'");
                }

                foreach (FileInfo file in result.ExportedDocuments)
                {
                    AddFile(entry, plcDirectory, file.FullName);
                }
            });
        }

        // SimaticML XML: always when asked, and as the fallback when documents are not available
        // for the language (GRAPH, STL, ...) so every block has at least one readable file.
        if (formats.Contains(FormatXml) || (formats.Contains(FormatDocuments) && !documentsOk))
        {
            var xmlOk = RunStep(errors, "xml", () =>
            {
                var file = new FileInfo(Path.Combine(directory, baseName + ".xml"));
                DeleteIfExists(file);
                block.Export(file, ExportOptions.None);
                AddFile(entry, plcDirectory, file.FullName);
            });
            if (xmlOk && !documentsOk)
            {
                // A successful fallback supersedes the documents failure for this item.
                errors.RemoveAll(message => message.StartsWith("documents:", StringComparison.Ordinal));
            }
        }

        if (formats.Contains(FormatSource))
        {
            var decision = SourceFormatEligibility.Decide(entry.BlockKind, entry.Language ?? string.Empty, entry.Path);
            if (decision.IsAllowed)
            {
                RunStep(errors, "source", () =>
                {
                    var path = Path.Combine(directory, baseName + decision.Extension);
                    DeleteIfExists(new FileInfo(path));
                    plcSoftware.ExternalSourceGroup.GenerateSource(
                        new List<IGenerateSource> { block },
                        new FileInfo(path),
                        GenerateOptions.None);
                    if (File.Exists(path))
                    {
                        AddFile(entry, plcDirectory, path);
                    }
                });
            }
        }
    }

    private static void ExportType(
        PlcType type,
        PlcSoftware plcSoftware,
        string plcDirectory,
        string directory,
        string baseName,
        IReadOnlyCollection<string> formats,
        ProjectFolderManifestEntry entry,
        List<string> errors)
    {
        var wantSource = formats.Contains(FormatSource) || formats.Contains(FormatDocuments);
        var sourceOk = false;
        if (wantSource)
        {
            sourceOk = RunStep(errors, "source", () =>
            {
                var path = Path.Combine(directory, baseName + ".udt");
                DeleteIfExists(new FileInfo(path));
                plcSoftware.ExternalSourceGroup.GenerateSource(
                    new List<IGenerateSource> { type },
                    new FileInfo(path),
                    GenerateOptions.None);
                if (File.Exists(path))
                {
                    AddFile(entry, plcDirectory, path);
                }
            });
        }

        if (formats.Contains(FormatXml) || !sourceOk)
        {
            var xmlOk = RunStep(errors, "xml", () =>
            {
                var file = new FileInfo(Path.Combine(directory, baseName + ".xml"));
                DeleteIfExists(file);
                type.Export(file, ExportOptions.None);
                AddFile(entry, plcDirectory, file.FullName);
            });
            if (xmlOk && !sourceOk)
            {
                errors.RemoveAll(message => message.StartsWith("source:", StringComparison.Ordinal));
            }
        }
    }

    private static IEnumerable<ExportItemDescriptor> Enumerate(PlcSoftware plcSoftware, IReadOnlyCollection<string> include)
    {
        var plcName = plcSoftware.Name;
        if (include.Contains(IncludeBlocks))
        {
            foreach (var item in EnumerateBlocks(plcSoftware.BlockGroup, plcName, new List<string> { "Blocks" }))
            {
                yield return item;
            }
        }

        if (include.Contains(IncludeTypes))
        {
            foreach (var item in EnumerateTypes(plcSoftware.TypeGroup, plcName, new List<string> { "Types" }))
            {
                yield return item;
            }
        }

        if (include.Contains(IncludeTagTables))
        {
            foreach (var item in EnumerateTagTables(plcSoftware.TagTableGroup, plcName, new List<string> { "Tags" }))
            {
                yield return item;
            }
        }
    }

    private static IEnumerable<ExportItemDescriptor> EnumerateBlocks(PlcBlockGroup group, string plcName, List<string> segments)
    {
        foreach (PlcBlock block in group.Blocks)
        {
            yield return new ExportItemDescriptor(KindBlock, BuildPath(plcName, segments, block.Name), segments.ToList(), block.Name, block);
        }

        foreach (PlcBlockUserGroup child in group.Groups)
        {
            foreach (var item in EnumerateBlocks(child, plcName, segments.Concat(new[] { child.Name }).ToList()))
            {
                yield return item;
            }
        }
    }

    private static IEnumerable<ExportItemDescriptor> EnumerateTypes(PlcTypeGroup group, string plcName, List<string> segments)
    {
        foreach (PlcType type in group.Types)
        {
            yield return new ExportItemDescriptor(KindType, BuildPath(plcName, segments, type.Name), segments.ToList(), type.Name, type);
        }

        foreach (PlcTypeUserGroup child in group.Groups)
        {
            foreach (var item in EnumerateTypes(child, plcName, segments.Concat(new[] { child.Name }).ToList()))
            {
                yield return item;
            }
        }
    }

    private static IEnumerable<ExportItemDescriptor> EnumerateTagTables(PlcTagTableGroup group, string plcName, List<string> segments)
    {
        foreach (PlcTagTable table in group.TagTables)
        {
            yield return new ExportItemDescriptor(KindTagTable, BuildPath(plcName, segments, table.Name), segments.ToList(), table.Name, table);
        }

        foreach (PlcTagTableUserGroup child in group.Groups)
        {
            foreach (var item in EnumerateTagTables(child, plcName, segments.Concat(new[] { child.Name }).ToList()))
            {
                yield return item;
            }
        }
    }

    private static string BuildPath(string plcName, IEnumerable<string> segments, string name)
        => string.Join("/", new[] { plcName }.Concat(segments).Concat(new[] { name }));

    private static bool MatchesPathFilter(string path, List<string>? prefixes)
    {
        if (prefixes is null || prefixes.Count == 0)
        {
            return true;
        }

        return prefixes.Any(prefix =>
            !string.IsNullOrWhiteSpace(prefix) &&
            path.StartsWith(prefix.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
    }

    private static bool RunStep(List<string> errors, string step, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception exception)
        {
            errors.Add($"{step}: {exception.Message}");
            return false;
        }
    }

    private static void AddFile(ProjectFolderManifestEntry entry, string plcDirectory, string fullPath)
    {
        var info = new FileInfo(fullPath);
        var relative = info.FullName.StartsWith(plcDirectory, StringComparison.OrdinalIgnoreCase)
            ? info.FullName.Substring(plcDirectory.Length).TrimStart('\\', '/')
            : info.FullName;
        entry.Files.Add(new ProjectFolderManifestFile
        {
            Name = relative.Replace('\\', '/'),
            Bytes = info.Exists ? info.Length : 0,
        });
    }

    private static void DeleteIfExists(FileInfo file)
    {
        if (file.Exists)
        {
            file.Delete();
        }
    }

    private static string ValidateExportRoot(string? exportRoot)
    {
        if (string.IsNullOrWhiteSpace(exportRoot))
        {
            throw new WorkerOperationException(
                WorkerFailureCategories.ValidationError,
                "export_to_folder is disabled: no export root is configured. Start tia-mcp with "
                + "--export-root <absolute folder> or set TIA_MCP_EXPORT_ROOT.");
        }

        if (!Path.IsPathRooted(exportRoot))
        {
            throw new WorkerOperationException(
                WorkerFailureCategories.ValidationError,
                "The configured export root must be an absolute path.");
        }

        var full = Path.GetFullPath(exportRoot!);
        Directory.CreateDirectory(full);
        return full;
    }

    private static void EnsureUnderRoot(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var fullCandidate = Path.GetFullPath(candidate).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (!fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkerOperationException(
                WorkerFailureCategories.ValidationError,
                "Refusing to write outside the configured export root.");
        }
    }

    internal static string SafeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "_";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name!.Length);
        foreach (var character in name!)
        {
            builder.Append(invalid.Contains(character) || character == '/' || character == '\\' ? '_' : character);
        }

        var safe = builder.ToString().Trim().TrimEnd('.');
        return safe.Length == 0 || safe == ".." ? "_" : safe;
    }

    private static HashSet<string> NormalizeSet(
        List<string>? requested,
        string[] allowed,
        string parameterName,
        string[]? defaults = null)
    {
        if (requested is null || requested.Count == 0)
        {
            return new HashSet<string>(defaults ?? allowed, StringComparer.Ordinal);
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in requested)
        {
            var match = allowed.FirstOrDefault(candidate => string.Equals(candidate, value?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                throw new WorkerOperationException(
                    WorkerFailureCategories.ValidationError,
                    $"Unknown {parameterName} value '{value}'. Valid values: {string.Join(", ", allowed)}.");
            }

            result.Add(match);
        }

        return result;
    }

    private static string BlockKindName(PlcBlock block) => block switch
    {
        GlobalDB => "GlobalDB",
        InstanceDB => "InstanceDB",
        ArrayDB => "ArrayDB",
        OB => "OB",
        FB => "FB",
        FC => "FC",
        _ => block.GetType().Name
    };

    private static string? SafeLanguage(PlcBlock block)
    {
        try
        {
            return block.ProgrammingLanguage.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int? SafeNumber(PlcBlock block)
    {
        try
        {
            return block.Number;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed class ExportItemDescriptor
    {
        public ExportItemDescriptor(string kind, string path, List<string> folderSegments, string name, object engineering)
        {
            Kind = kind;
            Path = path;
            FolderSegments = folderSegments;
            Name = name;
            Engineering = engineering;
        }

        public string Kind { get; }
        public string Path { get; }
        public List<string> FolderSegments { get; }
        public string Name { get; }
        public object Engineering { get; }
    }
}

internal sealed class ProjectFolderExportResult
{
    public string ExportFolder { get; set; } = string.Empty;
    public string PlcFolder { get; set; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string PlcName { get; set; } = string.Empty;
    public List<string> Include { get; set; } = new();
    public List<string> Formats { get; set; } = new();
    public int TotalItems { get; set; }
    public int Offset { get; set; }
    public int ProcessedItems { get; set; }
    public int SucceededItems { get; set; }
    public int FailedItems { get; set; }
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public bool Complete { get; set; }
    public int? NextOffset { get; set; }
    public long ElapsedMs { get; set; }
    public List<ProjectFolderExportFailure> Failures { get; set; } = new();
}

internal sealed class ProjectFolderExportFailure
{
    public string Path { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}

internal sealed class ProjectFolderManifestEntry
{
    public string Kind { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string? BlockKind { get; set; }
    public string? Language { get; set; }
    public int? Number { get; set; }
    public List<ProjectFolderManifestFile> Files { get; set; } = new();
    public string? Error { get; set; }
}

internal sealed class ProjectFolderManifestFile
{
    public string Name { get; set; } = string.Empty;
    public long Bytes { get; set; }
}
