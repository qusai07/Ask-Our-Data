using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;

namespace AskOurData.Web.Agent;

/// <summary>Loads the local model into memory at startup so the first question does not pay the load time.</summary>
public sealed class ModelWarmup(IChatClient model, IOptions<WebOptions> options, ILogger<ModelWarmup> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var o = new ChatOptions { Temperature = 0f, MaxOutputTokens = 1 };
            o.AddOllamaOption(OllamaOption.NumCtx, options.Value.Ollama.NumCtx);
            await model.GetResponseAsync([new ChatMessage(ChatRole.User, "ok")], o, stoppingToken);
            log.LogInformation("Model {Model} loaded.", options.Value.Ollama.Model);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Model warm-up failed ({Error}). Is Ollama running at {Endpoint}?", ex.Message, options.Value.Ollama.Endpoint);
        }
    }
}
