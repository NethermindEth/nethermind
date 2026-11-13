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
        ulong after,
        ulong? count,
        TraceFilterMode mode)
    {
        // Up to this length a linear scan is cheaper than hashing the address.
        private const int MaxScannedAddresses = 8;

        // An empty list is read as an omitted one, as in eth_getLogs: it does not restrict the match.
        private readonly Address[]? _fromAddresses = fromAddresses is { Length: > 0 } ? fromAddresses : null;
        private readonly Address[]? _toAddresses = toAddresses is { Length: > 0 } ? toAddresses : null;
        private readonly HashSet<AddressAsKey>? _fromAddressSet = ToSet(fromAddresses);
        private readonly HashSet<AddressAsKey>? _toAddressSet = ToSet(toAddresses);
        private ulong _after = after;
        private ulong? _count = count;
        private readonly TraceFilterMode _mode = mode;

        /// <summary>No further trace can be accepted, so the blocks left in the range cannot change the result.</summary>
        public bool IsExhausted => _count == 0;

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
            if (tx is not null && !IsExhausted && MatchAddresses(tx.From, tx.GetRecipient()))
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
            bool? fromMatch = Match(_fromAddresses, _fromAddressSet, fromAddress);
            bool? toMatch = Match(_toAddresses, _toAddressSet, toAddress);
            return _mode == TraceFilterMode.Union && (fromMatch.HasValue || toMatch.HasValue)
                ? fromMatch == true || toMatch == true
                : fromMatch != false && toMatch != false;
        }

        private static bool? Match(Address[]? addresses, HashSet<AddressAsKey>? addressSet, Address? address) =>
            addressSet?.Contains(address) ?? addresses?.Contains(address);

        private static HashSet<AddressAsKey>? ToSet(Address[]? addresses)
        {
            if (addresses is not { Length: > MaxScannedAddresses }) return null;

            HashSet<AddressAsKey> set = new(addresses.Length, GenericEqualityComparer.GetOptimized<AddressAsKey>());
            foreach (Address address in addresses)
            {
                set.Add(address);
            }

            return set;
        }
    }
}
