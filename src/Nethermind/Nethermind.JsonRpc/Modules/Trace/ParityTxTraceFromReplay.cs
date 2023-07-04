// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            Action = txTrace.Action;
            StateChanges = txTrace.StateChanges;
            TransactionHash = includeTransactionHash ? txTrace.TransactionHash : null;
        }

        public ParityTxTraceFromReplay(IReadOnlyCollection<ParityLikeTxTrace> txTraces, bool includeTransactionHash = false)
        {
            foreach (ParityLikeTxTrace txTrace in txTraces)
            {
                Output = txTrace.Output;
                VmTrace = txTrace.VmTrace;
                Action = txTrace.Action;
                StateChanges = txTrace.StateChanges;
                TransactionHash = includeTransactionHash ? txTrace.TransactionHash : null;
            }
        }

        public byte[]? Output { get; set; }

        public Keccak? TransactionHash { get; set; }

        public ParityVmTrace? VmTrace { get; set; }

        public ParityTraceAction? Action { get; set; }

        public Dictionary<Address, ParityAccountStateChange>? StateChanges { get; set; }
    }
}
