namespace PCBHelper.Core;

public sealed record ToolResponse<T>(
    bool Success,
    string Summary,
    T? Data,
    IReadOnlyList<string> Warnings,
    ToolError? Error)
{
    public static ToolResponse<T> Ok(string summary, T data, IReadOnlyList<string>? warnings = null)
    {
        return new ToolResponse<T>(true, summary, data, warnings ?? Array.Empty<string>(), null);
    }

    public static ToolResponse<T> Fail(string summary, string code, string? message = null, T? data = default)
    {
        return new ToolResponse<T>(false, summary, data, Array.Empty<string>(), new ToolError(code, message ?? summary));
    }
}

public sealed record ToolError(string Code, string Message);
