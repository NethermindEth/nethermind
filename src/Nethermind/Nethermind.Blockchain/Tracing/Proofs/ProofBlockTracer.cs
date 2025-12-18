// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Tracing.Proofs;

public class ProofBlockTracer(Hash256? txHash, bool treatZeroAccountDifferently)
    : BlockTracerBase<ProofTxTracer, ProofTxTracer>(txHash)
{
    protected override ProofTxTracer OnStart(Transaction? tx) => new(treatZeroAccountDifferently);

    /// <summary>
    /// Here I decided to return tracer after experimenting with ProofTxTrace class. It encapsulates less but avoid additional type introduction which does not bring much value.
    /// </summary>
    /// <param name="txTracer">Tracer of the transaction that just has been processed.</param>
    /// <returns>Just returns the <paramref name="txTracer"/></returns>
    protected override ProofTxTracer OnEnd(ProofTxTracer txTracer) => txTracer;
}
