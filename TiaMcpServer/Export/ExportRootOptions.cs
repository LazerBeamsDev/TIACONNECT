namespace TiaMcpServer.Export;

/// <summary>
/// ANet fork: the folder export_to_folder may write into. Taken from <c>--export-root</c> (or
/// <c>--export-root=</c>) and otherwise from the <c>TIA_MCP_EXPORT_ROOT</c> environment variable.
/// Null disables the tool's writes; the worker refuses the operation with a validation error.
/// </summary>
public sealed class ExportRootOptions
{
    public const string ArgumentName = "--export-root";
    public const string EnvironmentVariableName = "TIA_MCP_EXPORT_ROOT";

    public ExportRootOptions(string? exportRoot)
    {
        ExportRoot = string.IsNullOrWhiteSpace(exportRoot) ? null : exportRoot!.Trim().Trim('"');
    }

    public string? ExportRoot { get; }

    public static ExportRootOptions Resolve(string[] args, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], ArgumentName, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return new ExportRootOptions(args[i + 1]);
            }

            var prefix = ArgumentName + "=";
            if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return new ExportRootOptions(args[i].Substring(prefix.Length));
            }
        }

        return new ExportRootOptions(environment(EnvironmentVariableName));
    }
}
