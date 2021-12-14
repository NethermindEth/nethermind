using System;
using System.Timers;
using Nethermind.Logging;
using Nethermind.Blockchain;

namespace Nethermind.AccountAbstraction.Bundler
{
    public class PeriodicBundleTrigger : IBundleTrigger, IDisposable
    {
        private readonly Timer _timer;
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;

        public event EventHandler<BundleUserOpsEventArgs>? TriggerBundle;

        public PeriodicBundleTrigger(TimeSpan interval, IBlockTree blockTree, ILogger logger)
        {
            _blockTree = blockTree;
            _logger = logger;

            _timer = new Timer(interval.TotalMilliseconds);
            _timer.Elapsed += TimerOnElapsed;
            _timer.AutoReset = true;
            _timer.Start();

            _logger.Info("Trigger initialized");
        }

        private void TimerOnElapsed(object? sender, ElapsedEventArgs e)
        {
            _logger.Info("Trigger Called");
            try
            {
                TriggerBundle?.Invoke(this, new BundleUserOpsEventArgs(_blockTree.Head!));
            }
            catch (System.Exception ex)
            {
                _logger.Error(ex.Message);
                return;
            }
            _timer.Enabled = true;
        }

        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}
