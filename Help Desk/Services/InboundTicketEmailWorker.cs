using Microsoft.Extensions.Options;

namespace Help_Desk.Services;

public sealed class InboundTicketEmailWorker(IServiceScopeFactory scopeFactory, IOptionsMonitor<TicketEmailOptions> options, ILogger<InboundTicketEmailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested) {
            try {
                if (options.CurrentValue.IncomingEnabled) {
                    using var scope = scopeFactory.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<InboundTicketEmailProcessor>().ProcessBatchAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning("Inbound email check will retry ({ErrorType}).", exception.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
