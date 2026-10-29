using Microsoft.Extensions.Options;

namespace Help_Desk.Services;

public class TicketEmailWorker(IServiceScopeFactory scopeFactory, IOptionsMonitor<TicketEmailOptions> options, ILogger<TicketEmailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested) {
            try {
                if (options.CurrentValue.Enabled) {
                    using var scope = scopeFactory.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<TatOverdueEmails>().QueueAsync(stoppingToken);
                    await scope.ServiceProvider.GetRequiredService<TicketEmailDispatcher>().ProcessBatchAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning("Ticket email worker will retry after an error ({ErrorType}).", exception.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.CurrentValue.PollSeconds, 1, 60)), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}

