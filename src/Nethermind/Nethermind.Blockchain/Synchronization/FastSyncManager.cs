using System;
using Nethermind.Core.Logging;

namespace Nethermind.Blockchain.Synchronization
{
    public class FastSyncManager
    {
        private readonly ILogger _logger;

        public FastSyncManager(  ILogManager logManager)
        {
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }
    }
}