using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TiaMcpServer.Contracts;
using TiaMcpServer.Json;
using TiaMcpServer.ProjectTree;
using TiaMcpServer.Worker;

namespace TiaMcpServer.Tools;

/// <summary>Read-only project tools exposed in both access modes.</summary>
[McpServerToolType]
public class ProjectReadTools
{
    [McpServerTool(Name = "get_project_status", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Get status and metadata for the active TIA Portal project.")]
    public static async Task<string> GetProjectStatus(
        OpennessWorkerClient workerClient,
        [Description("Optional path to a .ap21 project file. If omitted, uses the project currently open in TIA Portal.")] string? projectPath = null)
    {
        var result = await workerClient.GetProjectStatusAsync(projectPath).ConfigureAwait(false);
        return StandaloneToolResultFormatter.Format(
            result,
            "Extended metadata (history, comments, languages) was too large to return in full.");
    }

    [McpServerTool(Name = "export_to_folder", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Export PLC blocks, PLC data types and tag tables of one PLC into files under the server's configured export root, "
        + "for objects too large to read through tool responses. The project is not modified. "
        + "Blocks: documents (.s7dcl/.s7res, readable LAD/FBD/SCL/DB) and source (.scl/.db where eligible), SimaticML .xml on request "
        + "and automatically when documents are unavailable (GRAPH, STL); types: .udt (xml fallback); tag tables: SimaticML .xml. "
        + "Work is chunked by time: repeat the call with the returned runId and nextOffset until complete is true. "
        + "The response lists the export folder and manifest.jsonl (one JSON line per exported item with relative file names); read the files from disk.")]
    public static async Task<string> ExportToFolder(
        OpennessWorkerClient workerClient,
        TiaMcpServer.Export.ExportRootOptions exportRootOptions,
        [Description("Optional PLC software or device name. Required only when the project has more than one PLC.")] string? plcName = null,
        [Description("Optional object kinds to export: blocks, types, tagTables. Defaults to all three.")] string[]? include = null,
        [Description("Optional block formats: documents, source, xml. Defaults to documents and source.")] string[]? formats = null,
        [Description("Optional item path prefixes such as PLC_1/Blocks/Folder, PLC_1/Types or PLC_1/Tags/Table. Only matching items are exported.")] string[]? pathPrefixes = null,
        [Description("Run id returned by the previous call; required together with offset to continue an export.")] string? runId = null,
        [Description("Index of the first item for this call; use nextOffset from the previous response. Defaults to 0.")] int? offset = null,
        [Description("Optional time budget of one call in seconds (1-240). Defaults to 35 so the call returns before client timeouts.")] int? timeBudgetSeconds = null,
        [Description("Optional path to a .ap21 project file. If omitted, uses the project currently open in TIA Portal.")] string? projectPath = null)
    {
        var result = await workerClient.ExportToFolderAsync(
            exportRootOptions.ExportRoot,
            projectPath,
            plcName,
            include,
            formats,
            pathPrefixes,
            runId,
            offset,
            timeBudgetSeconds).ConfigureAwait(false);
        return StandaloneToolResultFormatter.Format(
            result,
            "Narrow the export with pathPrefixes or include.");
    }

    [McpServerTool(
        Name = "browse_project_tree",
        ReadOnly = true,
        Destructive = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(BrowseProjectTreeResponse))]
    [Description("Browse a point-in-time TIA project tree through typed, bounded, resumable flat-node pages.")]
    public static async Task<CallToolResult> BrowseProjectTree(
        ProjectTreeBrowseCoordinator coordinator,
        [Description("Optional path to a .ap21 project file. If omitted, uses the project currently open in TIA Portal. On continuation, omit it or repeat the same project.")] string? projectPath = null,
        [Description("Optional ordered selector segments { nodeType, name }. Names match case-insensitively, node types exactly, and each segment must identify one direct child. On continuation, omit it or repeat the equivalent selector.")] ProjectTreeSelectorSegment[]? startSelector = null,
        [Description("Optional maximum depth from the selected root. Must be 1 or greater. On continuation, omit it or repeat the same depth.")] int? depth = null,
        [Description("Optional number of flat nodes requested for this page, from 1 through 200; defaults to 100 and may change between continuation pages.")] int? pageSize = null,
        [Description("Opaque cursor for the same point-in-time snapshot. Cursors can be replayed until idle expiry or eviction, but become unavailable after the server process restarts; restart without a cursor to observe again.")] string? cursor = null)
    {
        var rendered = await coordinator.BrowseAsync(
            new ProjectTreeBrowseRequest(projectPath, startSelector, depth, pageSize, cursor)).ConfigureAwait(false);
        return StructuredToolResult.CreateCanonical(rendered.CanonicalText, isError: !rendered.IsSuccess);
    }
}

internal static class ProjectReadToolRegistration
{
    internal static IMcpServerBuilder WithProjectReadTools(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var method in typeof(ProjectReadTools)
                     .GetMethods(BindingFlags.Public | BindingFlags.Static)
                     .Where(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>() is not null)
                     .OrderBy(candidate => candidate.MetadataToken))
        {
            var toolMethod = method;
            builder.Services.AddSingleton<McpServerTool>(services =>
            {
                var tool = McpServerTool.Create(
                    toolMethod,
                    target: null,
                    options: new McpServerToolCreateOptions
                    {
                        Services = services,
                        SchemaCreateOptions = new AIJsonSchemaCreateOptions
                        {
                            TransformSchemaNode = AlignSelectorSchemaNullability,
                            TransformOptions = new AIJsonSchemaTransformOptions
                            {
                                DisallowAdditionalProperties = true,
                            },
                        },
                    });

                return toolMethod.Name == nameof(ProjectReadTools.BrowseProjectTree)
                    ? new ProjectTreeArgumentValidatingTool(tool)
                    : tool;
            });
        }

        return builder;
    }

    private static JsonNode AlignSelectorSchemaNullability(
        AIJsonSchemaCreateContext context,
        JsonNode schema)
    {
        if (context.TypeInfo.Type == typeof(ProjectTreeSelectorSegment)
            && schema is JsonObject selectorSchema
            && selectorSchema["properties"] is JsonObject properties)
        {
            RemoveNullType(selectorSchema);
            RemoveNullType(properties["nodeType"]);
            RemoveNullType(properties["name"]);
        }

        return schema;
    }

    private static void RemoveNullType(JsonNode? schema)
    {
        if (schema is not JsonObject schemaObject
            || schemaObject["type"] is not JsonArray types)
        {
            return;
        }

        for (var index = types.Count - 1; index >= 0; index--)
        {
            if (types[index]?.GetValue<string>() == "null")
            {
                types.RemoveAt(index);
            }
        }
    }
}

internal sealed class ProjectTreeArgumentValidatingTool(McpServerTool innerTool)
    : DelegatingMcpServerTool(innerTool)
{
    private static readonly HashSet<string> AllowedArguments = new(StringComparer.Ordinal)
    {
        "projectPath",
        "startSelector",
        "depth",
        "pageSize",
        "cursor",
    };

    public override ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        var category = ShapeFailureCategory(request.Params.Arguments);
        return category is null
            ? base.InvokeAsync(request, cancellationToken)
            : ValueTask.FromResult(ValidationFailure(category));
    }

    private static string? ShapeFailureCategory(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        if (arguments.Keys.Any(key => !AllowedArguments.Contains(key))
            || !IsOptionalString(arguments, "projectPath")
            || !IsOptionalInteger(arguments, "depth")
            || !IsOptionalInteger(arguments, "pageSize"))
        {
            return WorkerFailureCategories.ValidationError;
        }

        if (!IsOptionalString(arguments, "cursor"))
        {
            return WorkerFailureCategories.InvalidCursor;
        }

        if (!arguments.TryGetValue("startSelector", out var selector)
            || selector.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (selector.ValueKind != JsonValueKind.Array)
        {
            return WorkerFailureCategories.InvalidSelector;
        }

        foreach (var segment in selector.EnumerateArray())
        {
            if (segment.ValueKind != JsonValueKind.Object)
            {
                return WorkerFailureCategories.InvalidSelector;
            }

            var properties = segment.EnumerateObject().ToArray();
            if (properties.Length != 2
                || !segment.TryGetProperty("nodeType", out var nodeType)
                || nodeType.ValueKind != JsonValueKind.String
                || !segment.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.String)
            {
                return WorkerFailureCategories.InvalidSelector;
            }
        }

        return null;
    }

    private static bool IsOptionalString(
        IDictionary<string, JsonElement> arguments,
        string name)
        => !arguments.TryGetValue(name, out var value)
           || value.ValueKind is JsonValueKind.Null or JsonValueKind.String;

    private static bool IsOptionalInteger(
        IDictionary<string, JsonElement> arguments,
        string name)
        => !arguments.TryGetValue(name, out var value)
           || value.ValueKind == JsonValueKind.Null
           || (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _));

    private static CallToolResult ValidationFailure(string category)
    {
        var response = new BrowseProjectTreeResponse(
            ProjectTreeContract.Version,
            ProjectTreeStatuses.Failed,
            Result: null,
            new BrowseProjectTreeFailure(
                category,
                "The browse_project_tree arguments did not match the declared input schema."),
            Array.Empty<string>());
        return StructuredToolResult.CreateCanonical(
            CanonicalJson.Serialize(response),
            isError: true);
    }
}
