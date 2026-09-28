// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Blockchain.Tracing.ParityStyle;

namespace Nethermind.JsonRpc.Modules.Trace
{
    public class TxTraceFilter(
        Address[]? fromAddresses,
        Address[]? toAddresses,
        int after,
        int? count,
        TraceFilterMode mode)
    {
        // An empty list is read as an omitted one, as in eth_getLogs: it does not restrict the match.
        private readonly Address[]? _fromAddresses = fromAddresses is { Length: > 0 } ? fromAddresses : null;
        private readonly Address[]? _toAddresses = toAddresses is { Length: > 0 } ? toAddresses : null;
        private int _after = after;
        private int? _count = count;
        private readonly TraceFilterMode _mode = mode;

        /// <summary>No further trace can be accepted, so the blocks left in the range cannot change the result.</summary>
        public bool IsExhausted => _count <= 0;

        public IEnumerable<ParityTxTraceFromStore> FilterTxTraces(IEnumerable<ParityTxTraceFromStore> txTraces)
        {
            foreach (ParityTxTraceFromStore? txTrace in txTraces)
            {
                if (IsExhausted)
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
            if (tx is not null && !IsExhausted && MatchAddresses(tx.From, tx.Type == "reward" ? tx.Author : tx.To))
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

        private bool MatchAddresses(Address? fromAddress, Address? toAddress)
        {
            bool? fromMatch = _fromAddresses?.Contains(fromAddress);
            bool? toMatch = _toAddresses?.Contains(toAddress);
            return _mode == TraceFilterMode.Union && (fromMatch.HasValue || toMatch.HasValue)
                ? fromMatch == true || toMatch == true
                : fromMatch != false && toMatch != false;
        }
    }
}
