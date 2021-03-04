//  Copyright (c) 2018 Demerzel Solutions Limited
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
// 

using System;
using System.Linq;
using Nethermind.TxPool;

namespace Nethermind.Blockchain.Producers
{
    // ReSharper disable once InconsistentNaming
    public static class IBlockProductionTriggerExtensions
    {
        public static IBlockProductionTrigger IfPoolIsNotEmpty(this IBlockProductionTrigger? trigger, ITxPool? txPool)
        {
            if (trigger == null) throw new ArgumentNullException(nameof(trigger));
            if (txPool == null) throw new ArgumentNullException(nameof(txPool));
            return new TriggerWithCondition(trigger, () => txPool.GetPendingTransactions().Any());
        }
        
        public static IBlockProductionTrigger Or(this IBlockProductionTrigger? trigger, IBlockProductionTrigger? alternative)
        {
            if (trigger == null) throw new ArgumentNullException(nameof(trigger));
            if (alternative == null) throw new ArgumentNullException(nameof(alternative));
            return new CompositeBlockProductionTrigger(trigger, alternative);
        }
    }
}
