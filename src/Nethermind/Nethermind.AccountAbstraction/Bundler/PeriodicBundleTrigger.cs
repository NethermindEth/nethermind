// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core.Timers;
using Nethermind.Logging;

namespace Nethermind.AccountAbstraction.Bundler
{
    public class PeriodicBundleTrigger : IBundleTrigger, IDisposable
    {
        private readonly ITimer _timer;
        private readonly IBlockTree _blockTree;
        private readonly ILogger _logger;

        public event EventHandler<BundleUserOpsEventArgs>? TriggerBundle;

        public PeriodicBundleTrigger(ITimerFactory timerFactory, TimeSpan interval, IBlockTree blockTree, ILogger logger)
        {
            _blockTree = blockTree;
            _logger = logger;

            _timer = timerFactory.CreateTimer(interval);

            _timer.Elapsed += TimerOnElapsed;
            _timer.Start();

            if (_logger.IsInfo) _logger.Info("Period bundle trigger initialized");
        }

        private void TimerOnElapsed(object? sender, EventArgs e)
        {
            if (_logger.IsTrace) _logger.Trace("Period Bundle Trigger Called");
            TriggerBundle?.Invoke(this, new BundleUserOpsEventArgs(_blockTree.Head!));
            _timer.Enabled = true;
        }

        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}
