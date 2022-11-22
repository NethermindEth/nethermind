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
        public static ParityTxTraceFromStore[] FromTxTrace(ParityLikeTxTrace txTrace)
        {
            List<ParityTxTraceFromStore> results = new();
            AddActionsRecursively(results, txTrace, txTrace.Action);
            return results.ToArray();
        }

        public static IEnumerable<ParityTxTraceFromStore> FromTxTrace(IReadOnlyCollection<ParityLikeTxTrace> txTrace)
        {
            List<ParityTxTraceFromStore> results = new();
            foreach (ParityLikeTxTrace tx in txTrace)
            {
                AddActionsRecursively(results, tx, tx.Action);
            }

            return results;

        }

        private static void AddActionsRecursively(List<ParityTxTraceFromStore> results, ParityLikeTxTrace txTrace, ParityTraceAction txTraceAction)
        {
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
            results.Add(result);

            foreach (ParityTraceAction subtrace in txTraceAction.Subtraces)
            {
                AddActionsRecursively(results, txTrace, subtrace);
            }
        }

        private ParityTxTraceFromStore()
        {
        }

        public ParityTraceAction Action { get; set; }

        public Keccak BlockHash { get; set; }

        [JsonConverter(typeof(LongConverter), NumberConversion.Raw)]
        public long BlockNumber { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Include)]
        public ParityTraceResult Result { get; set; }

        public int Subtraces { get; set; }

        public int[] TraceAddress { get; set; }

        public Keccak TransactionHash { get; set; }

        public int? TransactionPosition { get; set; }
        public string Type { get; set; }
        public string? Error { get; set; }
    }
}
