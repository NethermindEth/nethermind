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
using Nethermind.Evm.Tracing;
using Nethermind.Trie;

namespace Nethermind.Blockchain.Tracing
{
    /// <summary>
    /// A simple and flexible bridge for any tracing operations on blocks and transactions.
    /// </summary>
    public interface ITracer
    {
        /// <summary>
        /// Allows to trace an arbitrarily constructed block.
        /// </summary>
        /// <param name="block">Block to trace.</param>
        /// <param name="tracer">Trace to act on block processing events.</param>
        /// <returns>Post trace state root</returns>
        Keccak Trace(Block block, IBlockTracer tracer);
        
        void Accept(ITreeVisitor visitor, Keccak stateRoot);
    }
}
