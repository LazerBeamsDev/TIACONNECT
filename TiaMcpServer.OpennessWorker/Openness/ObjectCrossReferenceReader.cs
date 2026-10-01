using System.Text;
using System.Text.Json;
using Siemens.Engineering;
using Siemens.Engineering.CrossReference;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using TiaMcpServer.Contracts;

namespace TiaMcpServer.OpennessWorker.Openness;

/// <summary>
/// ANet fork: cross-references of exactly one object (block, PLC data type, tag table or tag),
/// answering "who reads / writes / calls this". Unlike the project-wide read_cross_references it
/// queries a single CrossReferenceService owner, so it returns within seconds even on large PLCs.
///
/// <para>
/// Output is flattened per referenced element (the object itself and, for DBs and types, each
/// member) with the referencing objects and their locations (network / line, access). When the
/// result would exceed the response budget, the full JSON is written under the export root and
/// the response keeps a per-referencing-object summary plus the file path.
/// </para>
/// </summary>
internal static class ObjectCrossReferenceReader
{
    private const int ResponseCharBudget = 45_000;
    private const int DefaultMaxLocations = 2_000;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static ObjectCrossReferenceResult Read(Project project, WorkerRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ObjectPath))
        {
            throw new WorkerOperationException(WorkerFailureCategories.ValidationError,
                "objectPath is required, e.g. PLC_1/Blocks/Folder/DB30, PLC_1/Tags/Table/Tag or a unique block name.");
        }

        var filterName = string.IsNullOrWhiteSpace(request.CrossReferenceFilter)
            ? "ObjectsWithReferences"
            : request.CrossReferenceFilter!;
        var filter = filterName.ToLowerInvariant() switch
        {
            "allobjects" => CrossReferenceFilter.AllObjects,
            "objectswithreferences" => CrossReferenceFilter.ObjectsWithReferences,
            "objectswithoutreferences" => CrossReferenceFilter.ObjectsWithoutReferences,
            "unusedobjects" => CrossReferenceFilter.UnusedObjects,
            _ => throw new WorkerOperationException(WorkerFailureCategories.ValidationError,
                "filter must be AllObjects, ObjectsWithReferences, ObjectsWithoutReferences or UnusedObjects."),
        };
        var maxLocations = request.MaxResults is > 0 ? request.MaxResults.Value : DefaultMaxLocations;

        var plcSoftware = PlcSoftwareLocator.Find(project, request.PlcName ?? PlcNameOf(request.ObjectPath!));
        var target = Resolve(plcSoftware, request.ObjectPath!.Trim());

        var service = target.Owner.GetService<CrossReferenceService>()
            ?? throw new WorkerOperationException(WorkerFailureCategories.WorkerOperationFailed,
                $"TIA Portal offers no cross-reference service for '{target.Path}'.");
        var query = service.GetCrossReferences(filter);

        var result = new ObjectCrossReferenceResult
        {
            ObjectPath = target.Path,
            ObjectKind = target.Kind,
            PlcName = plcSoftware.Name,
            Filter = filterName,
        };

        var locationCount = 0;
        foreach (SourceObject source in query.Sources)
        {
            Flatten(source, string.Empty, result, ref locationCount, maxLocations);
        }

        result.ElementCount = result.Elements.Count;
        result.ReferencingObjectCount = result.Elements.SelectMany(element => element.UsedBy)
            .Select(usage => usage.Name).Distinct(StringComparer.Ordinal).Count();
        result.LocationCount = locationCount;
        result.Summary = result.Elements
            .SelectMany(element => element.UsedBy.Select(usage => (element, usage)))
            .GroupBy(pair => pair.usage.Name, StringComparer.Ordinal)
            .Select(group => new ObjectCrossReferenceSummary
            {
                ReferencedBy = group.Key,
                TypeName = group.First().usage.TypeName,
                Elements = group.Select(pair => pair.element.Name).Distinct(StringComparer.Ordinal).Count(),
                Reads = group.Sum(pair => pair.usage.Locations.Count(location => location.Access.IndexOf("Read", StringComparison.OrdinalIgnoreCase) >= 0)),
                Writes = group.Sum(pair => pair.usage.Locations.Count(location => location.Access.IndexOf("Write", StringComparison.OrdinalIgnoreCase) >= 0)),
                Calls = group.Sum(pair => pair.usage.Locations.Count(location => location.Access.IndexOf("Call", StringComparison.OrdinalIgnoreCase) >= 0)),
                Locations = group.Sum(pair => pair.usage.Locations.Count),
            })
            .OrderByDescending(summary => summary.Locations)
            .ToList();

        var serialized = JsonSerializer.Serialize(result, Json);
        if (serialized.Length > ResponseCharBudget)
        {
            if (string.IsNullOrWhiteSpace(request.ExportRoot) || !Path.IsPathRooted(request.ExportRoot))
            {
                result.Elements = result.Elements.Take(20).ToList();
                result.Truncated = true;
                result.Note = "Result exceeded the response budget and no export root is configured; only the summary and the first 20 elements are returned. Narrow objectPath (single member/tag) or configure --export-root.";
            }
            else
            {
                var directory = Path.Combine(Path.GetFullPath(request.ExportRoot!),
                    ProjectFolderExporter.SafeName(project.Name), "xref");
                Directory.CreateDirectory(directory);
                var file = Path.Combine(directory,
                    ProjectFolderExporter.SafeName(target.Path.Replace('/', '_')) + "-" +
                    DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".json");
                File.WriteAllText(file, serialized, new UTF8Encoding(false));
                result.File = file;
                result.Elements = new List<ObjectCrossReferenceElement>();
                result.Truncated = true;
                result.Note = "Full element/location detail written to file; the response keeps the per-referencing-object summary.";
            }
        }

        return result;
    }

    private static void Flatten(
        SourceObject source,
        string prefix,
        ObjectCrossReferenceResult result,
        ref int locationCount,
        int maxLocations)
    {
        var name = Safe(() => source.Name);
        var fullName = string.IsNullOrEmpty(prefix) ? name : prefix + "." + name;
        var element = new ObjectCrossReferenceElement
        {
            Name = fullName,
            TypeName = Safe(() => source.TypeName),
            Address = NullIfEmpty(Safe(() => source.Address)),
        };

        foreach (ReferenceObject reference in SafeEnumerate(() => source.References))
        {
            var usage = new ObjectCrossReferenceUsage
            {
                Name = Safe(() => reference.Name),
                TypeName = Safe(() => reference.TypeName),
                Path = NullIfEmpty(Safe(() => reference.Path)),
            };

            foreach (Location location in SafeEnumerate(() => reference.Locations))
            {
                if (locationCount >= maxLocations)
                {
                    result.Truncated = true;
                    result.Note ??= $"Stopped after maxResults={maxLocations} locations.";
                    break;
                }

                usage.Locations.Add(new ObjectCrossReferenceLocation
                {
                    Name = Safe(() => location.Name),
                    Access = Safe(() => location.Access.ToString()),
                    ReferenceType = Safe(() => location.ReferenceType.ToString()),
                    ReferenceLocation = NullIfEmpty(Safe(() => location.ReferenceLocation)),
                    ReferencedAs = NullIfEmpty(Safe(() => location.ReferencedAs)),
                    Address = NullIfEmpty(Safe(() => location.Address)),
                });
                locationCount++;
            }

            element.UsedBy.Add(usage);
        }

        if (element.UsedBy.Count > 0 || result.Elements.Count == 0 || string.IsNullOrEmpty(prefix))
        {
            result.Elements.Add(element);
        }

        foreach (SourceObject child in SafeEnumerate(() => source.Children))
        {
            Flatten(child, fullName, result, ref locationCount, maxLocations);
        }
    }

    private static ResolvedObject Resolve(PlcSoftware plc, string objectPath)
    {
        var segments = objectPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segments.Count > 0 && string.Equals(segments[0], plc.Name, StringComparison.OrdinalIgnoreCase))
        {
            segments.RemoveAt(0);
        }

        if (segments.Count >= 2 && segments[0].Equals("Blocks", StringComparison.OrdinalIgnoreCase))
        {
            var group = WalkBlockGroups(plc.BlockGroup, segments.Skip(1).Take(segments.Count - 2), objectPath);
            var block = group.Blocks.Find(segments[segments.Count - 1])
                ?? throw NotFound(objectPath);
            return new ResolvedObject(block, "block", objectPath);
        }

        if (segments.Count >= 2 && segments[0].Equals("Types", StringComparison.OrdinalIgnoreCase))
        {
            PlcTypeGroup group = plc.TypeGroup;
            foreach (var name in segments.Skip(1).Take(segments.Count - 2))
            {
                group = group.Groups.Find(name) ?? throw NotFound(objectPath);
            }

            var type = group.Types.Find(segments[segments.Count - 1]) ?? throw NotFound(objectPath);
            return new ResolvedObject(type, "type", objectPath);
        }

        if (segments.Count >= 2 && segments[0].Equals("Tags", StringComparison.OrdinalIgnoreCase))
        {
            // PLC/Tags/<groups>/<table>  or  PLC/Tags/<groups>/<table>/<tag>
            PlcTagTableGroup group = plc.TagTableGroup;
            var rest = segments.Skip(1).ToList();
            for (var i = 0; i < rest.Count; i++)
            {
                var table = group.TagTables.Find(rest[i]);
                if (table is not null)
                {
                    if (i == rest.Count - 1)
                    {
                        return new ResolvedObject(table, "tagTable", objectPath);
                    }

                    var tagName = string.Join("/", rest.Skip(i + 1));
                    var tag = table.Tags.Find(tagName) ?? throw NotFound(objectPath);
                    return new ResolvedObject(tag, "tag", objectPath);
                }

                group = group.Groups.Find(rest[i]) ?? throw NotFound(objectPath);
            }

            throw NotFound(objectPath);
        }

        // Bare name: unique block, then tag, then type anywhere in the PLC.
        var name = segments.Count == 0 ? objectPath : segments[segments.Count - 1];
        var blocks = FindBlocks(plc.BlockGroup, name, "Blocks").ToList();
        if (blocks.Count == 1)
        {
            return new ResolvedObject(blocks[0].Block, "block", plc.Name + "/" + blocks[0].Path);
        }

        var tags = FindTags(plc.TagTableGroup, name, "Tags").ToList();
        if (blocks.Count == 0 && tags.Count == 1)
        {
            return new ResolvedObject(tags[0].Tag, "tag", plc.Name + "/" + tags[0].Path);
        }

        var types = FindTypes(plc.TypeGroup, name, "Types").ToList();
        if (blocks.Count == 0 && tags.Count == 0 && types.Count == 1)
        {
            return new ResolvedObject(types[0].Type, "type", plc.Name + "/" + types[0].Path);
        }

        var candidates = blocks.Select(b => b.Path).Concat(tags.Select(t => t.Path)).Concat(types.Select(t => t.Path)).Take(10).ToList();
        throw new WorkerOperationException(WorkerFailureCategories.ValidationError, candidates.Count == 0
            ? $"No block, tag or type named '{name}' in {plc.Name}."
            : $"'{name}' is ambiguous; use a full path: {string.Join("; ", candidates.Select(path => plc.Name + "/" + path))}.");
    }

    private static PlcBlockGroup WalkBlockGroups(PlcBlockGroup root, IEnumerable<string> names, string objectPath)
    {
        var group = root;
        foreach (var name in names)
        {
            group = group.Groups.Find(name) ?? throw NotFound(objectPath);
        }

        return group;
    }

    private static IEnumerable<(PlcBlock Block, string Path)> FindBlocks(PlcBlockGroup group, string name, string path)
    {
        foreach (PlcBlock block in group.Blocks)
        {
            if (string.Equals(block.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                yield return (block, path + "/" + block.Name);
            }
        }

        foreach (PlcBlockUserGroup child in group.Groups)
        {
            foreach (var match in FindBlocks(child, name, path + "/" + child.Name))
            {
                yield return match;
            }
        }
    }

    private static IEnumerable<(PlcTag Tag, string Path)> FindTags(PlcTagTableGroup group, string name, string path)
    {
        foreach (PlcTagTable table in group.TagTables)
        {
            var tag = table.Tags.Find(name);
            if (tag is not null)
            {
                yield return (tag, path + "/" + table.Name + "/" + tag.Name);
            }
        }

        foreach (PlcTagTableUserGroup child in group.Groups)
        {
            foreach (var match in FindTags(child, name, path + "/" + child.Name))
            {
                yield return match;
            }
        }
    }

    private static IEnumerable<(PlcType Type, string Path)> FindTypes(PlcTypeGroup group, string name, string path)
    {
        foreach (PlcType type in group.Types)
        {
            if (string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                yield return (type, path + "/" + type.Name);
            }
        }

        foreach (PlcTypeUserGroup child in group.Groups)
        {
            foreach (var match in FindTypes(child, name, path + "/" + child.Name))
            {
                yield return match;
            }
        }
    }

    private static string? PlcNameOf(string objectPath)
    {
        var first = objectPath.Split('/').FirstOrDefault();
        return objectPath.Contains("/") ? first : null;
    }

    private static WorkerOperationException NotFound(string objectPath)
        => new(WorkerFailureCategories.ValidationError,
            $"Object '{objectPath}' was not found. Use paths like PLC/Blocks/<groups>/<block>, PLC/Types/<groups>/<type>, PLC/Tags/<groups>/<table>[/<tag>] (as in export_to_folder manifest) or a unique name.");

    private static string Safe(Func<object?> read)
    {
        try
        {
            return read()?.ToString() ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    private static IEnumerable<object> SafeEnumerate(Func<System.Collections.IEnumerable> read)
    {
        System.Collections.IEnumerable? items;
        try
        {
            items = read();
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var item in items)
        {
            yield return item;
        }
    }

    private sealed class ResolvedObject
    {
        public ResolvedObject(IEngineeringServiceProvider owner, string kind, string path)
        {
            Owner = owner;
            Kind = kind;
            Path = path;
        }

        public IEngineeringServiceProvider Owner { get; }
        public string Kind { get; }
        public string Path { get; }
    }
}

internal sealed class ObjectCrossReferenceResult
{
    public string ObjectPath { get; set; } = string.Empty;
    public string ObjectKind { get; set; } = string.Empty;
    public string PlcName { get; set; } = string.Empty;
    public string Filter { get; set; } = string.Empty;
    public int ElementCount { get; set; }
    public int ReferencingObjectCount { get; set; }
    public int LocationCount { get; set; }
    public bool Truncated { get; set; }
    public string? Note { get; set; }
    public string? File { get; set; }
    public List<ObjectCrossReferenceSummary> Summary { get; set; } = new();
    public List<ObjectCrossReferenceElement> Elements { get; set; } = new();
}

internal sealed class ObjectCrossReferenceSummary
{
    public string ReferencedBy { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public int Elements { get; set; }
    public int Reads { get; set; }
    public int Writes { get; set; }
    public int Calls { get; set; }
    public int Locations { get; set; }
}

internal sealed class ObjectCrossReferenceElement
{
    public string Name { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public string? Address { get; set; }
    public List<ObjectCrossReferenceUsage> UsedBy { get; set; } = new();
}

internal sealed class ObjectCrossReferenceUsage
{
    public string Name { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public string? Path { get; set; }
    public List<ObjectCrossReferenceLocation> Locations { get; set; } = new();
}

internal sealed class ObjectCrossReferenceLocation
{
    public string Name { get; set; } = string.Empty;
    public string Access { get; set; } = string.Empty;
    public string ReferenceType { get; set; } = string.Empty;
    public string? ReferenceLocation { get; set; }
    public string? ReferencedAs { get; set; }
    public string? Address { get; set; }
}
