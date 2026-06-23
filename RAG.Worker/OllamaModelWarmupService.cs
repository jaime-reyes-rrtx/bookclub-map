using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using RAG.Core.Configuration;

namespace RAG.Worker;

public sealed class OllamaModelWarmupService(
    IHttpClientFactory httpClientFactory,
    IOptions<RagOptions> options,
    ILogger<OllamaModelWarmupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var ai = options.Value.Ai;
        if (!ai.Provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var client = httpClientFactory.CreateClient(nameof(OllamaModelWarmupService));
        client.BaseAddress = new Uri(ai.BaseUrl);
        client.Timeout = TimeSpan.FromMinutes(20);

        await PullModelAsync(client, ai.EmbeddingModel, cancellationToken);
        if (!ai.ChatModel.Equals(ai.EmbeddingModel, StringComparison.OrdinalIgnoreCase))
        {
            await PullModelAsync(client, ai.ChatModel, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private async Task PullModelAsync(HttpClient client, string model, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            try
            {
                logger.LogInformation("Ensuring Ollama model {Model} is available.", model);
                using var response = await client.PostAsJsonAsync(
                    "/api/pull",
                    new OllamaPullRequest(model, Stream: false),
                    cancellationToken);

                response.EnsureSuccessStatusCode();
                return;
            }
            catch (Exception ex) when (attempt < 12 && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Ollama model pull for {Model} failed on attempt {Attempt}.", model, attempt);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    private sealed record OllamaPullRequest(string Name, bool Stream);
}
