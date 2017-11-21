using System.Collections.Generic;
using Nevermind.Core;

namespace Nevermind.Evm
{
    public class TransactionSubstate
    {
        public TransactionSubstate(
            ulong refund,
            IReadOnlyCollection<Address> destroyList,
            IReadOnlyCollection<LogEntry> logs,
            bool shouldRevert)
        {
            Refund = refund;
            DestroyList = destroyList;
            Logs = logs;
            ShouldRevert = shouldRevert;
        }

        public bool ShouldRevert { get; }

        public ulong Refund { get; }

        public IReadOnlyCollection<LogEntry> Logs { get; }

        public IReadOnlyCollection<Address> DestroyList { get; }
    }
}