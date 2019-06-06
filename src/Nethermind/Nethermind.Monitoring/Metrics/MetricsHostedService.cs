using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Nethermind.Monitoring.Metrics
{
    public class MetricsHostedService : IHostedService, IDisposable
    {
        private const int IntervalSeconds = 5;
        private readonly IMetricsUpdater _metricsUpdater;
        private Timer _timer;

        public MetricsHostedService(IMetricsUpdater metricsUpdater)
        {
            _metricsUpdater = metricsUpdater;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _timer = new Timer(UpdateMetrics, null, TimeSpan.Zero, TimeSpan.FromSeconds(IntervalSeconds));

            return Task.CompletedTask;
        }

        private void UpdateMetrics(object state)
        {
            _metricsUpdater.StartUpdating();
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _timer?.Change(Timeout.Infinite, 0);

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}