using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AskOurData.Contracts;
using AskOurData.Web.Gateway;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;

namespace AskOurData.Web.Agent;

/// <summary>
/// Runs one question through Microsoft Agent Framework with the user's report tools.
/// Holds no database credentials: every data access is a gateway call with the user's token.
/// </summary>
public sealed class ReportAgent(
    IChatClient model,
    GatewayClient gateway,
    IOptions<WebOptions> options,
    ILogger<ReportAgent> log)
{
    private const string ClarifyTool = "ask_clarifying_question";
    private const string NotAvailableTool = "report_not_available";

    public async Task<AgentAnswer> AskAsync(
        string question,
        string language,
        string token,
        MeResponse me,
        IReadOnlyList<CatalogEntry> catalog,
        IReadOnlyList<GatewayClient.UnavailableReport> unavailable,
        IReadOnlyList<ConversationTurn> history,
        string conversationId,
        IProgress<AgentProgress>? progress,
        CancellationToken ct)
    {
        var o = options.Value;
        var sw = Stopwatch.StartNew();
        var ctx = new AgentRunContext(token, conversationId, progress);
        progress?.Report(new AgentProgress(ProgressStage.Thinking));

        // customer_ids the model may use: from earlier results in this conversation, or typed by the user.
        foreach (var t in history.SelectMany(h => h.Answer.Trace)) ctx.LearnCustomerIds(t.Result);
        foreach (var n in Grounding.AllowedNumbers([question]).Where(n => n > 0 && n == Math.Floor(n) && n <= int.MaxValue))
            ctx.KnownCustomerIds.Add((long)n);

        var tools = new List<AITool>();
        tools.AddRange(catalog.Select(e => new GatewayToolFunction(e, ctx, gateway, o.MaxToolCallsPerQuestion, o.RowsShownToModel)));
        tools.AddRange(unavailable.Select(u => new UnavailableReportFunction(u, ctx, gateway)));
        tools.Add(AIFunctionFactory.Create(
            ([Description("One short question for the user, in the user's language.")] string question,
             [Description("2 to 5 short answer options the user can click.")] string[]? options) =>
            {
                ctx.ClarifyQuestion = question;
                ctx.ClarifyOptions.AddRange((options ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Take(5));
                if (FunctionInvokingChatClient.CurrentContext is { } fc) fc.Terminate = true;
                return "The question is shown to the user. Stop here.";
            },
            ClarifyTool,
            "Ask the user ONE short clarifying question when the request is ambiguous and no sensible default exists."));
        tools.Add(AIFunctionFactory.Create(
            ([Description("Why no report fits, one sentence.")] string reason,
             [Description("Closest existing report name, if any.")] string? nearest_report) =>
            {
                ctx.NotAvailable = true;
                ctx.NearestReport = nearest_report;
                if (FunctionInvokingChatClient.CurrentContext is { } fc) fc.Terminate = true;
                return "Recorded as a missing report request. Stop here.";
            },
            NotAvailableTool,
            "Call when none of the reports can answer the question. The question is logged for the reporting team."));

        var chatOptions = new ChatOptions { Tools = tools, Temperature = 0f, ToolMode = ChatToolMode.Auto };
        chatOptions.AddOllamaOption(OllamaOption.NumCtx, o.Ollama.NumCtx);

        var client = new ChatClientBuilder(model)
            .UseFunctionInvocation(configure: f =>
            {
                f.MaximumIterationsPerRequest = o.MaxToolCallsPerQuestion + 1;
                f.AllowConcurrentInvocation = false;
                f.IncludeDetailedErrors = false;
            })
            .Build();

        var agent = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Name = "ask-our-data",
            ChatOptions = chatOptions,
            UseProvidedChatClientAsIs = true,
        });

        var messages = new List<ChatMessage> { new(ChatRole.System, SystemPrompt.Build(me, language, unavailable)) };
        messages.AddRange(HistoryMessages(history, o.HistoryTurns));
        messages.Add(new ChatMessage(ChatRole.User, question));

        string text;
        try
        {
            var response = await agent.RunAsync(messages, cancellationToken: ct);
            text = response.Text?.Trim() ?? "";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogError(ex, "Agent run failed (conversation {ConversationId})", conversationId);
            return Finish(AnswerKind.Error, null, false, ctx, sw, null);
        }

        progress?.Report(new AgentProgress(ProgressStage.Checking));
        var answer = await DecideAsync(question, language, text, ctx, unavailable, sw, ct);

        log.LogInformation(
            "Question answered: user={User} conversation={Conversation} outcome={Outcome} tools={Tools} withheld={Withheld} ms={Ms}",
            me.Oid, conversationId, answer.Kind, string.Join(",", ctx.Trace.Select(t => $"{t.Tool}:{(t.Ok ? "ok" : t.ErrorCode)}")),
            answer.ExplanationWithheld, answer.DurationMs);
        return answer;
    }

    private async Task<AgentAnswer> DecideAsync(string question, string language, string text, AgentRunContext ctx,
        IReadOnlyList<GatewayClient.UnavailableReport> unavailable, Stopwatch sw, CancellationToken ct)
    {
        if (ctx.ClarifyQuestion is not null)
            return Finish(AnswerKind.Clarify, null, false, ctx, sw, null);

        // The report exists but the user's role does not include it: that is a permission answer, not a missing report.
        if (ctx.NotAvailable && ctx.NearestReport is { } nearest && unavailable.Any(u => u.Name == nearest))
            return Finish(AnswerKind.Denied, null, false, ctx, sw, null);

        if (ctx.NotAvailable)
        {
            try { await gateway.LogMissingReportAsync(ctx.Token, ctx.ConversationId, question, ctx.NearestReport, ct); }
            catch (Exception ex) { log.LogWarning(ex, "Could not log missing report"); }
            return Finish(AnswerKind.OutOfScope, null, false, ctx, sw, null);
        }

        var ok = ctx.Trace.Where(t => t.Ok && t.Result is not null).ToList();
        var dataOk = ok.Where(t => t.Tool != "find_customer").ToList();
        if (dataOk.Count == 0)
        {
            var errors = ctx.Trace.Where(t => !t.Ok).Select(t => t.ErrorCode).ToList();
            if (errors.Contains(ErrorCodes.PermissionDenied)) return Finish(AnswerKind.Denied, null, false, ctx, sw, null);
            if (errors.Contains(ErrorCodes.NotFound)) return Finish(AnswerKind.NotFound, null, false, ctx, sw, null);
        }
        if (ok.Count == 0)
        {
            var errors = ctx.Trace.Where(t => !t.Ok).Select(t => t.ErrorCode).ToList();
            if (errors.Count > 0) return Finish(AnswerKind.Error, null, false, ctx, sw, null);

            // Text-only reply (greeting, refusal, explanation of capabilities). It must not contain data.
            var allowedNoData = Grounding.AllowedNumbers([question]);
            var bad = Grounding.Ungrounded(text, allowedNoData);
            return Finish(AnswerKind.Info, bad.Count == 0 && text.Length > 0 ? text : null, bad.Count > 0, ctx, sw, null);
        }

        // Grounding: every number in the explanation must come from a tool result (or the question).
        var sources = ok.Select(t => t.RawJson).Append(question).ToList();
        var allowed = Grounding.AllowedNumbers(sources);
        var explanation = text;
        var ungrounded = Grounding.Ungrounded(explanation, allowed);
        if (ungrounded.Count > 0 && explanation.Length > 0)
        {
            log.LogWarning("Ungrounded numbers in explanation: {Claims}; rewriting", string.Join(" | ", ungrounded.Select(c => c.Text)));
            explanation = await RewriteAsync(explanation, ok, language, ct);
            ungrounded = Grounding.Ungrounded(explanation, allowed);
        }
        var withheld = ungrounded.Count > 0 || explanation.Length == 0;
        return Finish(AnswerKind.Answer, withheld ? null : explanation, withheld, ctx, sw, DefaultNote(ok));
    }

    private async Task<string> RewriteAsync(string draft, List<ToolTrace> ok, string language, CancellationToken ct)
    {
        try
        {
            var data = string.Join("\n", ok.Select(t => ModelView.Render(t.Tool,
                new GatewayCallResult(true, 200, t.Result, null, null, t.RawJson), options.Value.RowsShownToModel)));
            var chatOptions = new ChatOptions { Temperature = 0f };
            chatOptions.AddOllamaOption(OllamaOption.NumCtx, options.Value.Ollama.NumCtx);
            var resp = await model.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, SystemPrompt.Rewrite(language)),
                new ChatMessage(ChatRole.User, $"DATA:\n{data}\n\nDRAFT:\n{draft}"),
            ], chatOptions, ct);
            return resp.Text?.Trim() ?? "";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Rewrite failed");
            return "";
        }
    }

    /// <summary>Server-known defaults the user did not ask for, e.g. metric=amount (shown as a note in the UI).</summary>
    private static string? DefaultNote(List<ToolTrace> ok)
    {
        foreach (var t in ok)
        {
            using var args = JsonDocument.Parse(t.ArgumentsJson);
            if (t.Tool == "get_top_customers_by_late_payments" && !args.RootElement.TryGetProperty("metric", out _))
                return "default_metric_amount";
        }
        return null;
    }

    private static AgentAnswer Finish(AnswerKind kind, string? text, bool withheld, AgentRunContext ctx, Stopwatch sw, string? defaultNote)
    {
        // Show the data results; hide the customer lookup when a real report followed it.
        var ok = ctx.Trace.Where(t => t.Ok && t.Result is not null).ToList();
        var shown = ok.Any(t => t.Tool != "find_customer") ? ok.Where(t => t.Tool != "find_customer") : ok;
        var results = kind == AnswerKind.Answer
            ? shown.GroupBy(t => t.Tool).Select(g => g.Last().Result!).ToList()
            : [];
        return new AgentAnswer(kind, text, withheld, ctx.ClarifyQuestion, ctx.ClarifyOptions.ToList(), results,
            ctx.Trace.ToList(), defaultNote, sw.ElapsedMilliseconds);
    }

    /// <summary>Compact history: questions and what was answered (report + parameters), not the raw data.</summary>
    private static IEnumerable<ChatMessage> HistoryMessages(IReadOnlyList<ConversationTurn> history, int turns)
    {
        foreach (var turn in history.TakeLast(turns))
        {
            yield return new ChatMessage(ChatRole.User, turn.Question);
            var sb = new StringBuilder();
            foreach (var t in turn.Answer.Trace.Where(t => t.Ok))
                sb.Append($"[Ran {t.Tool} with {t.ArgumentsJson}: {t.RowCount} rows] ");
            sb.Append(turn.Answer.Kind switch
            {
                AnswerKind.Clarify => turn.Answer.ClarifyQuestion,
                AnswerKind.OutOfScope => "No report fits this question.",
                AnswerKind.Denied => "The user does not have permission for that.",
                _ => turn.Answer.Text ?? "",
            });
            yield return new ChatMessage(ChatRole.Assistant, sb.ToString());
        }
    }

    public static IChatClient CreateOllama(OllamaOptions o) =>
        new OllamaApiClient(new HttpClient { BaseAddress = new Uri(o.Endpoint), Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds) }, o.Model);
}
