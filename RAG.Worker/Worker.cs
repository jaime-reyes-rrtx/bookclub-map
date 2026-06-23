using Microsoft.Extensions.Options;
using RAG.Core.Configuration;
using RAG.Core.Services;

namespace RAG.Worker;

public sealed class Worker(
    IServiceScopeFactory scopeFactory,
    IOptions<RagOptions> options,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(1, options.Value.Ingestion.PollIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var ingestion = scope.ServiceProvider.GetRequiredService<IDocumentIngestionService>();
                var processed = await ingestion.IngestPendingDocumentsAsync(stoppingToken);

                if (processed > 0)
                {
                    logger.LogInformation("Processed {DocumentCount} pending document(s).", processed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Worker polling failed.");
            }

            await Task.Delay(delay, stoppingToken);
        }
    }
}
