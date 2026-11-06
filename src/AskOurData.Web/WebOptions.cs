namespace AskOurData.Web;

public sealed class WebOptions
{
    public const string Section = "AskOurData";

    /// <summary>Query Gateway base URL. The web app has NO database credentials; everything goes through here.</summary>
    public string GatewayBaseUrl { get; set; } = "http://localhost:5247";

    /// <summary>Dev = demo user picker + gateway dev tokens. Entra = OIDC sign-in + on-behalf-of token (not wired yet).</summary>
    public string AuthMode { get; set; } = "Entra";

    public OllamaOptions Ollama { get; set; } = new();

    /// <summary>Hard cap on gateway calls per question (in addition to the gateway's own per-user rate limit).</summary>
    public int MaxToolCallsPerQuestion { get; set; } = 6;

    /// <summary>Previous turns sent to the model for follow-up questions.</summary>
    public int HistoryTurns { get; set; } = 4;

    /// <summary>Rows per tool result shown to the model (the UI always gets the full result).</summary>
    public int RowsShownToModel { get; set; } = 20;
}

public sealed class OllamaOptions
{
    public string Endpoint { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen2.5:7b";
    public int NumCtx { get; set; } = 16384;
    public int TimeoutSeconds { get; set; } = 180;
}
