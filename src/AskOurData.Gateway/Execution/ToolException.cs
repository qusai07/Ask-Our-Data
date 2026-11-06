using AskOurData.Contracts;

namespace AskOurData.Gateway.Execution;

/// <summary>Expected, caller-safe failure. Message is shown to the model, so it must never contain data.</summary>
public sealed class ToolException(string code, string message, int httpStatus) : Exception(message)
{
    public string Code { get; } = code;
    public int HttpStatus { get; } = httpStatus;

    public static ToolException Invalid(string message) => new(ErrorCodes.InvalidArgument, message, 400);
    public static ToolException Denied(string message) => new(ErrorCodes.PermissionDenied, message, 403);
    public static ToolException NotFound(string message) => new(ErrorCodes.NotFound, message, 404);
}
