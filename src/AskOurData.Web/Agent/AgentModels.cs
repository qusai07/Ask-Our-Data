using AskOurData.Contracts;

namespace AskOurData.Web.Agent;

public enum AnswerKind
{
    Answer,       // data returned, table(s) + explanation
    Clarify,      // agent asks one short question
    OutOfScope,   // no report fits; logged as a missing-report request
    Denied,       // gateway refused (role or region)
    NotFound,     // e.g. customer outside the user's scope
    Info,         // text-only reply (greeting, refusal to write SQL, ...)
    Error,
}

public sealed record ToolTrace(
    int Order,
    string Tool,
    string ArgumentsJson,
    bool Ok,
    string? ErrorCode,
    string? ErrorMessage,
    int? RowCount,
    long DurationMs,
    ToolResult? Result,
    string RawJson);

public sealed record AgentAnswer(
    AnswerKind Kind,
    string? Text,
    bool ExplanationWithheld,
    string? ClarifyQuestion,
    IReadOnlyList<string> ClarifyOptions,
    IReadOnlyList<ToolResult> Results,
    IReadOnlyList<ToolTrace> Trace,
    string? DefaultNote,
    long DurationMs);

public enum ProgressStage { Thinking, CallingTool, Checking }

public sealed record AgentProgress(ProgressStage Stage, string? Tool = null);

/// <summary>One finished exchange, used as compact history for follow-up questions.</summary>
public sealed record ConversationTurn(string Question, AgentAnswer Answer);
