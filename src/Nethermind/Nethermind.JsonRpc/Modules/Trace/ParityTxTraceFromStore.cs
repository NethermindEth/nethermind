// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Modules.Trace
{
    public class ParityTxTraceFromStore
    {
        public static IEnumerable<ParityTxTraceFromStore> FromTxTrace(ParityLikeTxTrace txTrace)
        {
            ParityTraceAction? txTraceAction = txTrace.Action;
            Stack<ParityTraceAction>? pending = null;
            while (txTraceAction is not null)
            {
                if (txTraceAction.IncludeInTrace)
                {
                    ParityTxTraceFromStore result = new();
                    result.Fill(txTrace, txTraceAction);
                    yield return result;

                    List<ParityTraceAction> subtraces = txTraceAction.Subtraces;
                    if (subtraces.Count > 0)
                    {
                        pending ??= new Stack<ParityTraceAction>();
                        for (int index = subtraces.Count - 1; index >= 0; index--)
                        {
                            pending.Push(subtraces[index]);
                        }
                    }
                }

                txTraceAction = pending is not null && pending.TryPop(out ParityTraceAction? next) ? next : null;
            }
        }

        public static IEnumerable<ParityTxTraceFromStore> FromTxTrace(IReadOnlyCollection<ParityLikeTxTrace> txTrace)
        {
            foreach (ParityLikeTxTrace tx in txTrace)
            {
                foreach (ParityTxTraceFromStore trace in FromTxTrace(tx))
                {
                    yield return trace;
                }
            }
        }

        [ThreadStatic]
        private static ParityTxTraceFromStore? _writeItem;

        /// <summary>
        /// Serializes the items <see cref="FromTxTrace(ParityLikeTxTrace)"/> would return, reusing one instance for all of them.
        /// </summary>
        /// <param name="filter">When set, only the actions it accepts are written.</param>
        /// <param name="cancellationToken">Checked before each action is written.</param>
        public static void Write(Utf8JsonWriter writer, ParityLikeTxTrace txTrace, TxTraceFilter? filter, JsonSerializerOptions options, CancellationToken cancellationToken = default)
        {
            if (txTrace.Action is null) return;

            ParityTxTraceFromStore item = _writeItem ??= new ParityTxTraceFromStore();
            try
            {
                WriteActionRecursively(writer, txTrace, txTrace.Action, filter, options, item, cancellationToken);
            }
            finally
            {
                // Drop the references to the trace tree so the thread does not keep it alive.
                item.Action = null!;
                item.Result = null!;
            }
        }

        private static void WriteActionRecursively(Utf8JsonWriter writer, ParityLikeTxTrace txTrace, ParityTraceAction action, TxTraceFilter? filter, JsonSerializerOptions options, ParityTxTraceFromStore item, CancellationToken cancellationToken)
        {
            if (!action.IncludeInTrace || filter is { IsExhausted: true }) return;

            cancellationToken.ThrowIfCancellationRequested();

            if (filter is null || filter.ShouldUseTxTrace(action))
            {
                item.Fill(txTrace, action);
                TypeInfoJsonSerializer.Serialize(writer, item, options);
            }

            List<ParityTraceAction> subtraces = action.Subtraces;
            for (int index = 0; index < subtraces.Count; index++)
            {
                WriteActionRecursively(writer, txTrace, subtraces[index], filter, options, item, cancellationToken);
            }
        }

        private void Fill(ParityLikeTxTrace txTrace, ParityTraceAction txTraceAction)
        {
            Action = txTraceAction;
            Result = txTraceAction.Result;
            Subtraces = txTraceAction.Subtraces.Count;
            Type = txTraceAction.Type;
            BlockHash = txTrace.BlockHash;
            BlockNumber = txTrace.BlockNumber;
            TransactionHash = txTrace.TransactionHash;
            TransactionPosition = txTrace.TransactionPosition;
            TraceAddress = txTraceAction.TraceAddress;
            Error = txTraceAction.Error;
        }

        private ParityTxTraceFromStore()
        {
        }

        [JsonConverter(typeof(ParityTraceActionConverter))]
        public ParityTraceAction Action { get; set; }

        public Hash256 BlockHash { get; set; }

        [JsonConverter(typeof(ULongRawJsonConverter))]
        public ulong BlockNumber { get; set; }

        /// <summary>The frame's result, written as null for a halted frame, a suicide and a reward.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public ParityTraceResult? Result { get; set; }

        public int Subtraces { get; set; }

        public CappedArray<int> TraceAddress { get; set; } = default;

        /// <summary>The transaction's hash, written as null for a reward, which belongs to no transaction.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public Hash256? TransactionHash { get; set; }

        /// <summary>The transaction's position in its block, written as null for a reward.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public int? TransactionPosition { get; set; }
        public string Type { get; set; }
        public string? Error { get; set; }
    }
}
