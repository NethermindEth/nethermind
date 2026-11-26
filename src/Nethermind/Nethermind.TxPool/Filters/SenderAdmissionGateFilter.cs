// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.TxPool.Filters;

/// <summary>
/// Enters the sender's admission gate for an EIP-8250 keyed-nonce frame transaction, so the MATCHA key-set and
/// width checks that follow see every earlier admission from that sender already inserted.
/// </summary>
/// <remarks>
/// The pool releases the gate once the submission settles. Entered under the head read lock, so a head update
/// never waits on a gate held by a thread that is itself waiting on the head lock.
/// </remarks>
internal sealed class SenderAdmissionGateFilter(SenderAdmissionGates gates) : IIncomingTxFilter
{
    public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions txHandlingOptions)
    {
        if (tx.SupportsFrames && KeyedNonceManager.UsesKeyedNonce(tx) && state.SenderAdmissionGate is null)
        {
            Lock gate = gates.For(tx.SenderAddress!);
            gate.Enter();
            state.SenderAdmissionGate = gate;
        }

        return AcceptTxResult.Accepted;
    }
}
