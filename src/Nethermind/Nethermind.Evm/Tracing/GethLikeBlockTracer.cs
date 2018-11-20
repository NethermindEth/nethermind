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

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm.Tracing
{
    public class GethLikeBlockTracer : IBlockTracer
    {
        private readonly Keccak _txHash;

        private bool _isTracingEntireBlock;

        public GethLikeBlockTracer(Block block)
        {
            _isTracingEntireBlock = true;
            _txTraces = new GethLikeTxTrace[block.Transactions.Length];
        }

        public GethLikeBlockTracer(Keccak txHash)
        {
            _txHash = txHash;
            _txTraces = new GethLikeTxTrace[1];
        }

        private int _currentTxIndex;
        
        private GethLikeTxTracer _currentTxTracer;
        
        ITxTracer IBlockTracer.StartNewTxTrace(Keccak txHash)
        {
            if (_isTracingEntireBlock || _txHash == txHash)
            {
                _currentTxTracer = new GethLikeTxTracer();
                return _currentTxTracer;
            }
            
            if(!_isTracingEntireBlock && _txHash != txHash)
            {
                throw new InvalidOperationException($"Unexpected tx trace started - awaiting {_txHash}, received {txHash}");
            }
            
            return NullTxTracer.Instance;
        }

        void IBlockTracer.EndTxTrace()
        {
            if (_currentTxTracer == null)
            {
                throw new InvalidOperationException("Cannot end tx trace that has not been started");
            }
                
            _txTraces[_currentTxIndex++] = _currentTxTracer.BuildResult();
            _currentTxTracer = null;
        }
        
        private GethLikeTxTrace[] _txTraces;

        public GethLikeBlockTrace BuildResult()
        {
            return new GethLikeBlockTrace(_txTraces);
        }
    }
}