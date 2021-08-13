//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm.Tracing.Proofs
{
    public class ProofBlockTracer : BlockTracerBase<ProofTxTracer, ProofTxTracer>, IBlockTracer
    {
        private readonly bool _treatSystemAccountDifferently;

        public ProofBlockTracer(Keccak txHash, bool treatSystemAccountDifferently) : base(txHash)
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

        public override void StartNewBlockTrace(Block block)
        {
        }

        public override void EndBlockTrace()
        {
        }
    }
}
