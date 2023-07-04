// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Trace
{
    public class TxTraceFilter
    {
        private readonly Address[]? _fromAddresses;
        private readonly Address[]? _toAddresses;
        private int _after;
        private int? _count;

        public TxTraceFilter(
            Address[]? fromAddresses,
            Address[]? toAddresses,
            int after,
            int? count)
        {
            _fromAddresses = fromAddresses;
            _toAddresses = toAddresses;
            _after = after;
            _count = count;
        }

        public IEnumerable<ParityTxTraceFromStore> FilterTxTraces(IEnumerable<ParityTxTraceFromStore> txTraces)
        {
            foreach (ParityTxTraceFromStore? txTrace in txTraces)
            {
                if (_count <= 0)
                {
                    break;
                }

                if (ShouldUseTxTrace(txTrace.Action))
                {
                    yield return txTrace;
                }
            }

        }

        public bool ShouldUseTxTrace(ParityTraceAction? tx)
        {
            if (tx is not null && !(_count <= 0) && MatchAddresses(tx.From, tx.To))
            {
                if (_after > 0)
                {
                    --_after;
                    return false;
                }

                --_count;
                return true;
            }

            return false;
        }

        private bool MatchAddresses(Address? fromAddress, Address? toAddress) =>
            _fromAddresses?.Contains(fromAddress) != false && _toAddresses?.Contains(toAddress) != false;
    }
}
