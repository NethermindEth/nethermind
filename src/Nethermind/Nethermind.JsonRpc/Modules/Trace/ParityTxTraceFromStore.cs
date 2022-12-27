// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Trace
{
    public class ParityTxTraceFromStore
    {
        public static IEnumerable<ParityTxTraceFromStore> FromTxTrace(ParityLikeTxTrace txTrace)
        {
            return ReturnActionsRecursively(txTrace, txTrace.Action);
        }

        public static IEnumerable<ParityTxTraceFromStore> FromTxTrace(IReadOnlyCollection<ParityLikeTxTrace> txTrace)
        {
            foreach (ParityLikeTxTrace tx in txTrace)
            {
                foreach (ParityTxTraceFromStore trace in ReturnActionsRecursively(tx, tx.Action))
                {
                    yield return trace;
                }
            }
        }

        private static IEnumerable<ParityTxTraceFromStore> ReturnActionsRecursively(ParityLikeTxTrace txTrace, ParityTraceAction? txTraceAction)
        {
            if (txTraceAction is null || !txTraceAction.IncludeInTrace) yield break;

            ParityTxTraceFromStore result = new()
            {
                Action = txTraceAction,
                Result = txTraceAction.Result,
                Subtraces = txTraceAction.Subtraces.Count,
                Type = txTraceAction.Type,
                BlockHash = txTrace.BlockHash,
                BlockNumber = txTrace.BlockNumber,
                TransactionHash = txTrace.TransactionHash,
                TransactionPosition = txTrace.TransactionPosition,
                TraceAddress = txTraceAction.TraceAddress,
                Error = txTraceAction.Error
            };
            yield return result;

            for (int index = 0; index < txTraceAction.Subtraces.Count; index++)
            {
                ParityTraceAction subtrace = txTraceAction.Subtraces[index];
                foreach (ParityTxTraceFromStore convertedSubtrace in ReturnActionsRecursively(txTrace, subtrace))
                {
                    yield return convertedSubtrace;
                }

            }
        }

        private ParityTxTraceFromStore()
        {
        }

        public ParityTraceAction Action { get; set; }

        public Keccak BlockHash { get; set; }

        [JsonConverter(typeof(LongConverter), NumberConversion.Raw)]
        public long BlockNumber { get; set; }

        public ParityTraceResult Result { get; set; }

        public int Subtraces { get; set; }

        public int[] TraceAddress { get; set; }

        public Keccak TransactionHash { get; set; }

        public int? TransactionPosition { get; set; }
        public string Type { get; set; }
        public string? Error { get; set; }
    }
}
