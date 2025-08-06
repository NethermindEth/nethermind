using Microsoft.Extensions.Logging;

namespace Lantern.Discv5.WireProtocol.Utility;

public class GracefulTaskRunner(ILoggerFactory loggerFactory) : IGracefulTaskRunner
{
    private readonly ILogger<GracefulTaskRunner> _logger = loggerFactory.CreateLogger<GracefulTaskRunner>();

    public async Task RunWithGracefulCancellationAsync(Func<CancellationToken, Task> taskFunc, string description, CancellationToken cancellationToken)
    {
        try
        {
            await taskFunc(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("{Description}Async was canceled gracefully", description);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred in {Description}Async", description);
        }
    }
}