namespace ScriptStacks;

public sealed record Request
{
    public string Operation { get; init; } = "capture";
    public string Runtime { get; init; } = "";
    public int Pid { get; init; }
    public ulong CreationTime { get; init; }
    public string? CancelEventName { get; init; }
    public bool AllowActivate { get; init; }
    public bool CloseInspector { get; init; } = true;
}

public sealed record CaptureResult(string Status, string Text, string? ToolId = null, string? InstallLabel = null)
{
    public static CaptureResult Ok(string text) => new("ok", text);
    public static CaptureResult Error(string text) => new("error", text);
    public static CaptureResult Missing(string id, string label, string text) => new("missing_tool", text, id, label);
}