/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm.Tracing
{
    public class ParityLikeBlockTracer : BlockTracerBase<ParityLikeTxTrace, ParityLikeTxTracer>
    {
        private readonly Block _block;
        private readonly ParityTraceTypes _types;

        public ParityLikeBlockTracer(Block block, Keccak txHash, ParityTraceTypes types)
            :base(txHash)
        {
            _block = block;
            _types = types;
        }
        
        public ParityLikeBlockTracer(Block block, ParityTraceTypes types)
            :base(block)
        {
            _block = block;
            _types = types;
        }

        protected override ParityLikeTxTracer OnStart(Keccak txHash)
        {
            return new ParityLikeTxTracer(_block, _block.Transactions.Single(t => t.Hash == txHash), _types);
        }

        protected override ParityLikeTxTrace OnEnd(ParityLikeTxTracer txTracer)
        {
            return txTracer.BuildResult();
        }
    }
}