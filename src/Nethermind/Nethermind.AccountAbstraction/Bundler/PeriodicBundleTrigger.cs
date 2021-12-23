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

            if (_logger.IsInfo) _logger.Info("Period bundle trigger initialized");
        }

        private void TimerOnElapsed(object? sender, ElapsedEventArgs e)
        {
            if (_logger.IsTrace) _logger.Trace("Period Bundle Trigger Called");
            try
            {
                TriggerBundle?.Invoke(this, new BundleUserOpsEventArgs(_blockTree.Head!));
            }
            catch (System.Exception ex)
            {
                _logger.Error(ex.ToString());
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
