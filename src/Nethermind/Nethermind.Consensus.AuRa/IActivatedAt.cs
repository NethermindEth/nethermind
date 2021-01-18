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

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Core;

namespace Nethermind.Consensus.AuRa
{
    public interface IActivatedAt<out T> where T : IComparable<T>
    {
        T Activation { get; }
    }
	
	public interface IActivatedAt : IActivatedAt<long>
    {
    }
    
    public interface IActivatedAtBlock : IActivatedAt
    {
        public long ActivationBlock => Activation;
    }

    internal static class ActivatedAtBlockExtensions
    {
        public static void BlockActivationCheck(this IActivatedAtBlock activatedAtBlock, BlockHeader parentHeader)
        {
            if (parentHeader.Number + 1 < activatedAtBlock.ActivationBlock) throw new InvalidOperationException($"{activatedAtBlock.GetType().Name} is not active for block {parentHeader.Number + 1}. Its activated on block {activatedAtBlock.ActivationBlock}.");
        }
    }
}
