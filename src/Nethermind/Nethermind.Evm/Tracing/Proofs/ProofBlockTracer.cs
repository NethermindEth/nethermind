// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm.Tracing.Proofs
{
    public class ProofBlockTracer : BlockTracerBase<ProofTxTracer, ProofTxTracer>
    {
        private readonly bool _treatSystemAccountDifferently;

        public ProofBlockTracer(Keccak? txHash, bool treatSystemAccountDifferently) : base(txHash)
        {
            _treatSystemAccountDifferently = treatSystemAccountDifferently;
        }

        protected override ProofTxTracer OnStart(Transaction? tx) => new(_treatSystemAccountDifferently);

        /// <summary>
        /// Here I decided to return tracer after experimenting with ProofTxTrace class. It encapsulates less but avoid additional type introduction which does not bring much value.
        /// </summary>
        /// <param name="txTracer">Tracer of the transaction that just has been processed.</param>
        /// <returns>Just returns the <paramref name="txTracer"/></returns>
        protected override ProofTxTracer OnEnd(ProofTxTracer txTracer) => txTracer;
    }
}
