using System;
using System.Threading;

namespace Nethermind.Monitoring.Metrics
{
    public class MetricsUpdater : IMetricsUpdater
    {
        private readonly int _intervalSeconds;
        private Timer _timer;
        private readonly MetricsRegistry _metrics = new MetricsRegistry();

        public MetricsUpdater(int intervalSeconds = 5)
        {
            _intervalSeconds = intervalSeconds;
        }
        
        public void StartUpdating()
        {
            _timer = new Timer(UpdateMetrics, null, TimeSpan.Zero, TimeSpan.FromSeconds(_intervalSeconds));
        }

        public void StopUpdating()
        {
            _timer?.Change(Timeout.Infinite, 0);
        }

        private void UpdateMetrics(object state)
        {
            _metrics.UpdateMetrics(typeof(Blockchain.Metrics));
            _metrics.UpdateMetrics(typeof(Evm.Metrics));
            _metrics.UpdateMetrics(typeof(Store.Metrics));
            _metrics.UpdateMetrics(typeof(Network.Metrics));
            _metrics.UpdateMetrics(typeof(JsonRpc.Metrics));
        }
    }
}