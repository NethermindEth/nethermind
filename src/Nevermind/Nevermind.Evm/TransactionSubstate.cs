using System.Collections.Generic;
using System.Numerics;
using Nevermind.Core;

namespace Nevermind.Evm
{
    public class TransactionSubstate
    {
        public TransactionSubstate(
            BigInteger refund,
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

        public BigInteger Refund { get; }

        public IReadOnlyCollection<LogEntry> Logs { get; }

        public IReadOnlyCollection<Address> DestroyList { get; }
    }
}