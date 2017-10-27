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
            IReadOnlyCollection<LogEntry> logs)
        {
            Refund = refund;
            DestroyList = destroyList;
            Logs = logs;
        }

        public BigInteger Refund { get; }

        public IReadOnlyCollection<LogEntry> Logs { get; }

        public IReadOnlyCollection<Address> DestroyList { get; }
    }
}