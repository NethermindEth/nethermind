using System.Collections.Generic;
using System.Numerics;
using Nevermind.Core;

namespace Nevermind.Evm
{
    public class TransactionSubstate
    {
        public BigInteger RefundCounter { get; set; }

        public List<LogEntry> Logs { get; set; }

        public List<Address> DestroyList { get; set; }
    }
}