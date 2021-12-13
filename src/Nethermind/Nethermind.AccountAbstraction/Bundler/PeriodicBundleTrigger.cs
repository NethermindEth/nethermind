using System;
using System.Timers;
using Nethermind.Blockchain;

namespace Nethermind.AccountAbstraction.Bundler
{
    public class PeriodicBundleTrigger : IBundleTrigger, IDisposable
    {
        private readonly Timer _timer;
        private readonly IBlockTree _blockTree;

        public event EventHandler<BundleUserOpsEventArgs>? TriggerBundle;

        public PeriodicBundleTrigger(TimeSpan interval, IBlockTree blockTree)
        {
            _blockTree = blockTree;

            _timer = new Timer(interval.TotalMilliseconds);
            _timer.Elapsed += TimerOnElapsed;
            _timer.AutoReset = false;
            _timer.Start();
        }

        private void TimerOnElapsed(object? sender, ElapsedEventArgs e)
        {
            TriggerBundle?.Invoke(this, new BundleUserOpsEventArgs(_blockTree.Head!));
            _timer.Enabled = true;
        }

        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}
