// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
// 
// 
// 

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing.ParityStyle;

namespace Nethermind.JsonRpc.Modules.Trace
{
    public class ParityTxTraceFromReplay
    {
        public ParityTxTraceFromReplay()
        {
        }

        public ParityTxTraceFromReplay(ParityLikeTxTrace txTrace, bool includeTransactionHash = false)
        {
            Output = txTrace.Output;
            VmTrace = txTrace.VmTrace;
            Action = GetAction(txTrace.Action);
            StateChanges = txTrace.StateChanges;
            TransactionHash = includeTransactionHash ? txTrace.TransactionHash : null;
        }

        public ParityTxTraceFromReplay(IReadOnlyCollection<ParityLikeTxTrace> txTraces, bool includeTransactionHash = false)
        {
            foreach (ParityLikeTxTrace txTrace in txTraces)
            {
                Output = txTrace.Output;
                VmTrace = txTrace.VmTrace;
                Action = GetAction(txTrace.Action);
                StateChanges = txTrace.StateChanges;
                TransactionHash = includeTransactionHash ? txTrace.TransactionHash : null;
            }
        }

        private static ParityTraceAction? GetAction(ParityTraceAction? action)
        {
            RemoveEmptyResults(action);

            return action;
        }

        private static void RemoveEmptyResults(ParityTraceAction? action)
        {
            if (action is not null)
            {
                if (action.Result?.IsEmpty == true)
                {
                    action.Result = null;
                }

                for (int i = 0; i < action.Subtraces.Count; i++)
                {
                    RemoveEmptyResults(action.Subtraces[i]);
                }
            }
        }

        public byte[]? Output { get; set; }

        public Keccak? TransactionHash { get; set; }

        public ParityVmTrace? VmTrace { get; set; }

        public ParityTraceAction? Action { get; set; }

        public Dictionary<Address, ParityAccountStateChange>? StateChanges { get; set; }
    }
}
