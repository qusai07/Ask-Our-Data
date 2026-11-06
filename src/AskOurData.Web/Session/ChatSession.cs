using AskOurData.Contracts;
using AskOurData.Web.Agent;
using AskOurData.Web.Gateway;
using Microsoft.AspNetCore.Components.Authorization;

namespace AskOurData.Web.Session;

public enum SessionStatus { Loading, Ready, Disabled, GatewayUnavailable, SignedOut }

/// <summary>Per-circuit conversation state. The gateway token stays on the server.</summary>
public sealed class ChatSession(
    AuthenticationStateProvider auth,
    IGatewayTokenProvider tokens,
    GatewayClient gateway,
    ReportAgent agent,
    ILogger<ChatSession> log)
{
    private string? _token;

    public SessionStatus Status { get; private set; } = SessionStatus.Loading;
    public MeResponse? Me { get; private set; }
    public IReadOnlyList<CatalogEntry> Catalog { get; private set; } = [];
    public IReadOnlyList<GatewayClient.UnavailableReport> Unavailable { get; private set; } = [];
    public List<ConversationTurn> Turns { get; } = [];
    public string ConversationId { get; private set; } = NewId();

    public async Task InitializeAsync(CancellationToken ct)
    {
        try
        {
            var state = await auth.GetAuthenticationStateAsync();
            _token = await tokens.GetTokenAsync(state.User, ct);
            if (_token is null) { Status = SessionStatus.SignedOut; return; }

            Me = await gateway.GetMeAsync(_token, ct);
            if (Me is null) { Status = SessionStatus.GatewayUnavailable; return; }
            if (!Me.Enabled) { Status = SessionStatus.Disabled; return; }

            Catalog = await gateway.GetCatalogAsync(_token, ct);
            Unavailable = await gateway.GetUnavailableAsync(_token, ct);
            Status = SessionStatus.Ready;
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning(ex, "Gateway unavailable");
            Status = SessionStatus.GatewayUnavailable;
        }
    }

    public async Task<AgentAnswer> AskAsync(string question, string language, IProgress<AgentProgress> progress, CancellationToken ct)
    {
        if (Status != SessionStatus.Ready || _token is null || Me is null)
            throw new InvalidOperationException("Session is not ready.");
        var answer = await agent.AskAsync(question, language, _token, Me, Catalog, Unavailable, Turns, ConversationId, progress, ct);
        Turns.Add(new ConversationTurn(question, answer));
        return answer;
    }

    public void Reset()
    {
        Turns.Clear();
        ConversationId = NewId();
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}
