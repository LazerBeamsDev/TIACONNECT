using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using TiaMcpServer.Contracts;

namespace TiaMcpServer.OpennessWorker.Openness;

/// <summary>
/// ANet fork: read-only dump of a WinCC Unified HMI software (screens with every screen item,
/// its geometry, properties, tag dynamizations and event scripts; HMI tag tables; alarms;
/// connections) into JSON files under the export root.
///
/// <para>
/// Everything is read through the generic <see cref="IEngineeringObject"/> surface
/// (GetAttributeInfos / GetAttributes / GetCompositionInfos / GetComposition) so the worker needs
/// no compile-time reference to Siemens.Engineering.WinCCUnified.dll: the Unified object model is
/// walked by composition names (Screens, ScreenGroups, ScreenItems, Dynamizations, EventHandlers,
/// TagTables, Tags, DiscreteAlarms, AnalogAlarms, AlarmClasses, Connections). Chunked by a time
/// budget like <see cref="ProjectFolderExporter"/>.
/// </para>
/// </summary>
internal static class HmiUnifiedExporter
{
    internal const string IncludeScreens = "screens";
    internal const string IncludeTags = "tags";
    internal const string IncludeAlarms = "alarms";
    internal const string IncludeConnections = "connections";

    internal const string ModeLayout = "layout";
    internal const string ModeAll = "all";

    private const int MaxNestingDepth = 3;
    private const int MaxListItems = 50;
    private const int MaxReportedFailures = 25;
    private static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(35);
    private static readonly Regex RunIdPattern = new("^[0-9]{8}-[0-9]{6}$", RegexOptions.CultureInvariant);

    /// <summary>Attributes kept in layout mode (when the object exposes them).</summary>
    private static readonly HashSet<string> LayoutAttributes = new(StringComparer.Ordinal)
    {
        "Name", "Left", "Top", "Width", "Height", "Visible", "Enabled", "Layer", "TabIndex",
        "BackColor", "ForeColor", "BorderColor", "BorderWidth", "CornerRadius", "BackgroundFillPattern",
        "Text", "Font", "TextColor", "AlignmentHorizontal", "AlignmentVertical", "TextTrimming", "WordWrap",
        "ProcessValue", "OutputFormat", "InputBehavior", "Mode", "Graphic", "GraphicStretchMode",
        "RotationAngle", "RotationCenterPlacement", "Points", "X1", "Y1", "X2", "Y2", "LineWidth",
        "LineColor", "StartArrow", "EndArrow", "Radius", "RadiusX", "RadiusY", "StartAngle", "Angle",
        "MinValue", "MaxValue", "Value", "Values", "Label", "Title", "Caption", "Tooltip",
        "Screen", "ScreenName", "ContentScreen", "ScreenNumber", "Faceplate", "Interface", "Properties",
        "Selection", "SelectionItems", "Items", "States", "Background", "Border", "Padding", "Margin",
        "DataType", "Connection", "PLCTag", "PlcTag", "Address", "AcquisitionCycle", "AcquisitionMode",
        "PropertyName", "DynamizationType", "Tag", "ReadOnly", "UseIndirectAddressing", "IndexTag",
        "Script", "ScriptCode", "GlobalDefinitionAreaScriptCode", "EventType", "Function", "Functions",
        "Condition", "Trigger", "Culture", "Language", "Items", "Inverted", "Bit", "Mappings",
        "Comment", "Priority", "Class", "AlarmClass", "RaisedStateTag", "TriggerTag", "TriggerBit",
        "LimitTag", "AlarmText", "Origin", "Area", "Station", "Partner", "CommunicationDriver",
        "InitialValue", "Color", "Size", "FontName", "FontWeight", "Italic", "Underline",
        "ContainedType", "FaceplateType", "Type", "TypeName", "LibraryObject", "Version", "Graphics",
    };

    public static HmiUnifiedExportResult Export(Project project, WorkerRequest request)
    {
        var exportRoot = ValidateExportRoot(request.ExportRoot);
        var include = NormalizeSet(request.ExportInclude,
            new[] { IncludeScreens, IncludeTags, IncludeAlarms, IncludeConnections }, "include");
        var mode = string.IsNullOrWhiteSpace(request.ExportAttributeMode)
            ? ModeAll
            : request.ExportAttributeMode!.Trim().ToLowerInvariant();
        if (mode != ModeAll && mode != ModeLayout)
        {
            throw new WorkerOperationException(WorkerFailureCategories.ValidationError,
                "attributes must be 'all' or 'layout'.");
        }

        var offset = request.ExportOffset ?? 0;
        var runId = ResolveRunId(request.ExportRunId, offset);
        var hmi = FindHmi(project, request.ExportDeviceName);
        var hmiDirectory = Path.Combine(exportRoot, ProjectFolderExporter.SafeName(project.Name), runId,
            ProjectFolderExporter.SafeName(hmi.Name));
        EnsureUnderRoot(exportRoot, hmiDirectory);
        Directory.CreateDirectory(hmiDirectory);

        var items = EnumerateItems(hmi.Software, include)
            .Where(item => MatchesFilter(item, request.ExportPathPrefixes))
            .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (offset < 0 || offset > items.Count)
        {
            throw new WorkerOperationException(WorkerFailureCategories.ValidationError,
                $"offset {offset} is outside 0..{items.Count}.");
        }

        var budget = request.ExportTimeBudgetSeconds is > 0 and <= 240
            ? TimeSpan.FromSeconds(request.ExportTimeBudgetSeconds.Value)
            : DefaultBudget;
        var stopwatch = Stopwatch.StartNew();
        var runDirectory = Path.GetDirectoryName(hmiDirectory)!;
        var manifestPath = Path.Combine(runDirectory, "manifest-hmi.jsonl");
        var result = new HmiUnifiedExportResult
        {
            ExportFolder = runDirectory,
            HmiFolder = hmiDirectory,
            ManifestPath = manifestPath,
            RunId = runId,
            HmiName = hmi.Name,
            DeviceName = hmi.DeviceName,
            SoftwareType = hmi.Software.GetType().FullName ?? hmi.Software.GetType().Name,
            AttributeMode = mode,
            Include = include.ToList(),
            TotalItems = items.Count,
            Offset = offset,
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
                var entry = new HmiManifestEntry { Kind = item.Kind, Path = item.Path };
                try
                {
                    var node = DumpItem(item, mode, entry);
                    var file = Path.Combine(new[] { hmiDirectory }
                        .Concat(item.FolderSegments.Select(ProjectFolderExporter.SafeName))
                        .Concat(new[] { ProjectFolderExporter.SafeName(item.Name) + ".json" })
                        .ToArray());
                    EnsureUnderRoot(hmiDirectory, file);
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllText(file, node.ToJsonString(IndentedJson), new UTF8Encoding(false));
                    entry.File = file.Substring(hmiDirectory.Length).TrimStart('\\', '/').Replace('\\', '/');
                    entry.Bytes = new FileInfo(file).Length;
                    result.SucceededItems++;
                    result.TotalBytes += entry.Bytes;
                }
                catch (Exception exception)
                {
                    entry.Error = exception.Message;
                    result.FailedItems++;
                    if (result.Failures.Count < MaxReportedFailures)
                    {
                        result.Failures.Add(new ProjectFolderExportFailure { Path = item.Path, Error = exception.Message });
                    }
                }

                manifest.WriteLine(JsonSerializer.Serialize(entry, ManifestJson));
                result.ProcessedItems++;
                index++;
            }
        }

        result.Complete = index >= items.Count;
        result.NextOffset = result.Complete ? null : index;
        result.ElapsedMs = stopwatch.ElapsedMilliseconds;
        return result;
    }

    private static JsonObject DumpItem(HmiItem item, string mode, HmiManifestEntry entry)
    {
        switch (item.Kind)
        {
            case "screen":
            {
                var screen = DumpObject(item.Engineering, mode, depth: 0);
                var itemsArray = new JsonArray();
                foreach (var screenItem in Composition(item.Engineering, "ScreenItems"))
                {
                    var node = DumpObject(screenItem, mode, depth: 0);
                    AddChildren(node, screenItem, "Dynamizations", mode);
                    AddChildren(node, screenItem, "EventHandlers", mode);
                    AddChildren(node, screenItem, "PropertyEventHandlers", mode);
                    itemsArray.Add(node);
                }

                AddChildren(screen, item.Engineering, "EventHandlers", mode);
                screen["screenItems"] = itemsArray;
                entry.Count = itemsArray.Count;
                return screen;
            }

            case "tagTable":
            {
                var table = DumpObject(item.Engineering, mode, depth: 0);
                var tags = new JsonArray();
                foreach (var tag in Composition(item.Engineering, "Tags"))
                {
                    tags.Add(DumpObject(tag, mode, depth: 0));
                }

                table["tags"] = tags;
                entry.Count = tags.Count;
                return table;
            }

            default:
            {
                // Flat collections directly under the HMI software (alarms, connections).
                var root = new JsonObject { ["kind"] = item.Kind };
                var count = 0;
                foreach (var compositionName in item.CompositionNames)
                {
                    var array = new JsonArray();
                    foreach (var child in Composition(item.Engineering, compositionName))
                    {
                        array.Add(DumpObject(child, mode, depth: 0));
                    }

                    root[LowerFirst(compositionName)] = array;
                    count += array.Count;
                }

                entry.Count = count;
                return root;
            }
        }
    }

    private static void AddChildren(JsonObject target, IEngineeringObject owner, string compositionName, string mode)
    {
        var children = Composition(owner, compositionName).ToList();
        if (children.Count == 0)
        {
            return;
        }

        var array = new JsonArray();
        foreach (var child in children)
        {
            array.Add(DumpObject(child, mode, depth: 1));
        }

        target[LowerFirst(compositionName)] = array;
    }

    /// <summary>Dumps one engineering object: its type and readable attributes.</summary>
    private static JsonObject DumpObject(IEngineeringObject engineering, string mode, int depth)
    {
        var node = new JsonObject { ["$type"] = engineering.GetType().Name };
        List<string> names;
        try
        {
            names = engineering.GetAttributeInfos()
                .Where(info => info.AccessMode == EngineeringAttributeAccessMode.Read
                    || info.AccessMode == EngineeringAttributeAccessMode.ReadWrite)
                .Select(info => info.Name)
                .Where(name => mode == ModeAll || depth > 0 || LayoutAttributes.Contains(name))
                .ToList();
        }
        catch (Exception exception)
        {
            node["$error"] = "attributes: " + exception.Message;
            return node;
        }

        IList<object?>? values = null;
        try
        {
            values = engineering.GetAttributes(names).Cast<object?>().ToList();
        }
        catch (Exception)
        {
            // One unreadable attribute fails the batch; fall back to one call per attribute.
        }

        for (var i = 0; i < names.Count; i++)
        {
            object? value;
            if (values is not null && i < values.Count)
            {
                value = values[i];
            }
            else
            {
                try
                {
                    value = engineering.GetAttribute(names[i]);
                }
                catch (Exception)
                {
                    continue;
                }
            }

            var converted = ConvertValue(value, mode, depth);
            if (converted is not null)
            {
                node[LowerFirst(names[i])] = converted;
            }
        }

        // Multilingual texts keep their translations in an "Items" composition.
        if (depth < MaxNestingDepth && HasComposition(engineering, "Items") &&
            engineering.GetType().Name.IndexOf("Multilingual", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var items = new JsonArray();
            foreach (var translation in Composition(engineering, "Items"))
            {
                items.Add(DumpObject(translation, ModeAll, depth + 1));
            }

            node["items"] = items;
        }

        return node;
    }

    private static JsonNode? ConvertValue(object? value, string mode, int depth)
    {
        switch (value)
        {
            case null:
                return null;
            case string text:
                return text.Length == 0 ? null : JsonValue.Create(text);
            case bool flag:
                return JsonValue.Create(flag);
            case int or long or short or byte or sbyte or ushort or uint or ulong:
                return JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            case float or double or decimal:
                return JsonValue.Create(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            case Enum enumValue:
                return JsonValue.Create(enumValue.ToString());
            case CultureInfo culture:
                return JsonValue.Create(culture.Name);
            case DateTime dateTime:
                return JsonValue.Create(dateTime.ToString("o", CultureInfo.InvariantCulture));
            case TimeSpan timeSpan:
                return JsonValue.Create(timeSpan.ToString());
            case IEngineeringObject nested:
                if (depth >= MaxNestingDepth || IsReferencedProjectObject(nested))
                {
                    return JsonValue.Create(SafeName(nested));
                }

                return DumpObject(nested, mode, depth + 1);
        }

        var type = value.GetType();
        if (type.FullName == "System.Drawing.Color")
        {
            var argb = type.GetMethod("ToArgb")?.Invoke(value, null);
            return argb is int packed ? JsonValue.Create("#" + packed.ToString("X8", CultureInfo.InvariantCulture)) : JsonValue.Create(value.ToString());
        }

        if (value is IEnumerable enumerable)
        {
            var array = new JsonArray();
            foreach (var element in enumerable)
            {
                if (array.Count >= MaxListItems)
                {
                    array.Add(JsonValue.Create("…"));
                    break;
                }

                array.Add(ConvertValue(element, mode, depth + 1) ?? JsonValue.Create((string?)null));
            }

            return array;
        }

        return JsonValue.Create(value.ToString());
    }

    /// <summary>
    /// A nested value that is itself a project object with compositions (a screen in a screen
    /// window, a tag, a connection) is written as its name instead of being dumped again.
    /// Scripts, fonts, multilingual texts and other value-like objects are dumped in full.
    /// </summary>
    private static bool IsReferencedProjectObject(IEngineeringObject nested)
    {
        var typeName = nested.GetType().Name;
        if (typeName.IndexOf("Script", StringComparison.OrdinalIgnoreCase) >= 0 ||
            typeName.IndexOf("Multilingual", StringComparison.OrdinalIgnoreCase) >= 0 ||
            typeName.EndsWith("Part", StringComparison.Ordinal) ||
            typeName.IndexOf("Converter", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return false;
        }

        try
        {
            return nested.GetCompositionInfos().Any(info => !string.Equals(info.Name, "Items", StringComparison.Ordinal));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string SafeName(IEngineeringObject engineering)
    {
        try
        {
            return engineering.GetAttribute("Name")?.ToString() ?? engineering.GetType().Name;
        }
        catch (Exception)
        {
            return engineering.GetType().Name;
        }
    }

    private static IEnumerable<HmiItem> EnumerateItems(IEngineeringObject software, IReadOnlyCollection<string> include)
    {
        if (include.Contains(IncludeScreens))
        {
            foreach (var screen in Composition(software, "Screens"))
            {
                yield return new HmiItem("screen", "Screens/" + SafeName(screen), new List<string> { "Screens" }, SafeName(screen), screen);
            }

            foreach (var item in EnumerateScreenGroups(software, "ScreenGroups", new List<string> { "Screens" }))
            {
                yield return item;
            }
        }

        if (include.Contains(IncludeTags))
        {
            foreach (var table in Composition(software, "TagTables"))
            {
                yield return new HmiItem("tagTable", "Tags/" + SafeName(table), new List<string> { "Tags" }, SafeName(table), table);
            }

            foreach (var item in EnumerateTagTableGroups(software, "TagTableGroups", new List<string> { "Tags" }))
            {
                yield return item;
            }
        }

        if (include.Contains(IncludeAlarms))
        {
            yield return new HmiItem("alarms", "Alarms", new List<string>(), "alarms", software,
                new[] { "AlarmClasses", "DiscreteAlarms", "AnalogAlarms" }.Where(name => HasComposition(software, name)).ToArray());
        }

        if (include.Contains(IncludeConnections))
        {
            yield return new HmiItem("connections", "Connections", new List<string>(), "connections", software,
                new[] { "Connections" }.Where(name => HasComposition(software, name)).ToArray());
        }
    }

    private static IEnumerable<HmiItem> EnumerateScreenGroups(IEngineeringObject owner, string compositionName, List<string> segments)
    {
        foreach (var group in Composition(owner, compositionName))
        {
            var groupSegments = segments.Concat(new[] { SafeName(group) }).ToList();
            foreach (var screen in Composition(group, "Screens"))
            {
                yield return new HmiItem("screen", string.Join("/", groupSegments) + "/" + SafeName(screen), groupSegments, SafeName(screen), screen);
            }

            foreach (var item in EnumerateScreenGroups(group, "Groups", groupSegments))
            {
                yield return item;
            }
        }
    }

    private static IEnumerable<HmiItem> EnumerateTagTableGroups(IEngineeringObject owner, string compositionName, List<string> segments)
    {
        foreach (var group in Composition(owner, compositionName))
        {
            var groupSegments = segments.Concat(new[] { SafeName(group) }).ToList();
            foreach (var table in Composition(group, "TagTables"))
            {
                yield return new HmiItem("tagTable", string.Join("/", groupSegments) + "/" + SafeName(table), groupSegments, SafeName(table), table);
            }

            foreach (var item in EnumerateTagTableGroups(group, "Groups", groupSegments))
            {
                yield return item;
            }
        }
    }

    private static bool HasComposition(IEngineeringObject engineering, string name)
    {
        try
        {
            return engineering.GetCompositionInfos().Any(info => string.Equals(info.Name, name, StringComparison.Ordinal));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IEnumerable<IEngineeringObject> Composition(IEngineeringObject engineering, string name)
    {
        if (!HasComposition(engineering, name))
        {
            return Array.Empty<IEngineeringObject>();
        }

        object? composition;
        try
        {
            composition = engineering.GetComposition(name);
        }
        catch (Exception)
        {
            return Array.Empty<IEngineeringObject>();
        }

        if (composition is IEngineeringObject single && composition is not IEnumerable)
        {
            return new[] { single };
        }

        return composition is IEnumerable enumerable
            ? enumerable.OfType<IEngineeringObject>().ToList()
            : Array.Empty<IEngineeringObject>();
    }

    private static DiscoveredHmi FindHmi(Project project, string? deviceName)
    {
        var found = new List<DiscoveredHmi>();
        foreach (Device device in ProjectDeviceEnumerator.Enumerate(project))
        {
            foreach (var software in FindSoftware(device.DeviceItems))
            {
                var typeName = software.GetType().FullName ?? string.Empty;
                if (typeName.IndexOf("HmiUnified", StringComparison.OrdinalIgnoreCase) < 0 &&
                    software.GetType().Name != "HmiSoftware")
                {
                    continue;
                }

                found.Add(new DiscoveredHmi(device.Name, SafeName(software), software));
            }
        }

        if (found.Count == 0)
        {
            throw new WorkerOperationException(WorkerFailureCategories.ValidationError,
                "No WinCC Unified HMI software was found in the project.");
        }

        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            var match = found.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, deviceName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
            return match ?? throw new WorkerOperationException(WorkerFailureCategories.ValidationError,
                $"No Unified HMI named '{deviceName}'. Available: {string.Join(", ", found.Select(candidate => candidate.DeviceName + "/" + candidate.Name))}.");
        }

        if (found.Count > 1)
        {
            throw new WorkerOperationException(WorkerFailureCategories.ValidationError,
                $"Several Unified HMIs exist; pass hmiName. Available: {string.Join(", ", found.Select(candidate => candidate.DeviceName + "/" + candidate.Name))}.");
        }

        return found[0];
    }

    private static IEnumerable<IEngineeringObject> FindSoftware(DeviceItemComposition items)
    {
        foreach (DeviceItem item in items)
        {
            IEngineeringObject? software = null;
            try
            {
                software = item.GetService<SoftwareContainer>()?.Software as IEngineeringObject;
            }
            catch (EngineeringException exception)
            {
                Console.Error.WriteLine($"Skipping a device item while locating HMI software: {exception.Message}");
            }

            if (software is not null)
            {
                yield return software;
            }

            foreach (var child in FindSoftware(item.DeviceItems))
            {
                yield return child;
            }
        }
    }

    private static bool MatchesFilter(HmiItem item, List<string>? prefixes)
        => prefixes is null || prefixes.Count == 0 || prefixes.Any(prefix =>
            !string.IsNullOrWhiteSpace(prefix) &&
            item.Path.StartsWith(prefix.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

    private static string ResolveRunId(string? runId, int offset)
    {
        if (runId is null)
        {
            if (offset != 0)
            {
                throw new WorkerOperationException(WorkerFailureCategories.ValidationError,
                    "offset greater than 0 requires the runId returned by the first call.");
            }

            return DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        }

        if (!RunIdPattern.IsMatch(runId))
        {
            throw new WorkerOperationException(WorkerFailureCategories.ValidationError,
                "runId must be the value returned by a previous call (yyyyMMdd-HHmmss).");
        }

        return runId;
    }

    private static string ValidateExportRoot(string? exportRoot)
    {
        if (string.IsNullOrWhiteSpace(exportRoot) || !Path.IsPathRooted(exportRoot))
        {
            throw new WorkerOperationException(WorkerFailureCategories.ValidationError,
                "export_hmi_to_folder is disabled: no absolute export root is configured "
                + "(--export-root or TIA_MCP_EXPORT_ROOT).");
        }

        var full = Path.GetFullPath(exportRoot!);
        Directory.CreateDirectory(full);
        return full;
    }

    private static void EnsureUnderRoot(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var fullCandidate = Path.GetFullPath(candidate);
        if (!fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fullCandidate.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkerOperationException(WorkerFailureCategories.ValidationError,
                "Refusing to write outside the configured export root.");
        }
    }

    private static HashSet<string> NormalizeSet(List<string>? requested, string[] allowed, string parameterName)
    {
        if (requested is null || requested.Count == 0)
        {
            return new HashSet<string>(allowed, StringComparer.Ordinal);
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in requested)
        {
            var match = allowed.FirstOrDefault(candidate => string.Equals(candidate, value?.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new WorkerOperationException(WorkerFailureCategories.ValidationError,
                    $"Unknown {parameterName} value '{value}'. Valid values: {string.Join(", ", allowed)}.");
            result.Add(match);
        }

        return result;
    }

    private static string LowerFirst(string name)
        => string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class DiscoveredHmi
    {
        public DiscoveredHmi(string deviceName, string name, IEngineeringObject software)
        {
            DeviceName = deviceName;
            Name = name;
            Software = software;
        }

        public string DeviceName { get; }
        public string Name { get; }
        public IEngineeringObject Software { get; }
    }

    private sealed class HmiItem
    {
        public HmiItem(string kind, string path, List<string> folderSegments, string name, IEngineeringObject engineering, string[]? compositionNames = null)
        {
            Kind = kind;
            Path = path;
            FolderSegments = folderSegments;
            Name = name;
            Engineering = engineering;
            CompositionNames = compositionNames ?? Array.Empty<string>();
        }

        public string Kind { get; }
        public string Path { get; }
        public List<string> FolderSegments { get; }
        public string Name { get; }
        public IEngineeringObject Engineering { get; }
        public string[] CompositionNames { get; }
    }
}

internal sealed class HmiUnifiedExportResult
{
    public string ExportFolder { get; set; } = string.Empty;
    public string HmiFolder { get; set; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string HmiName { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string SoftwareType { get; set; } = string.Empty;
    public string AttributeMode { get; set; } = string.Empty;
    public List<string> Include { get; set; } = new();
    public int TotalItems { get; set; }
    public int Offset { get; set; }
    public int ProcessedItems { get; set; }
    public int SucceededItems { get; set; }
    public int FailedItems { get; set; }
    public long TotalBytes { get; set; }
    public bool Complete { get; set; }
    public int? NextOffset { get; set; }
    public long ElapsedMs { get; set; }
    public List<ProjectFolderExportFailure> Failures { get; set; } = new();
}

internal sealed class HmiManifestEntry
{
    public string Kind { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string? File { get; set; }
    public long Bytes { get; set; }
    public int? Count { get; set; }
    public string? Error { get; set; }
}
