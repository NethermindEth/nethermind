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
// 

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.TxPool;

namespace Nethermind.Blockchain.Producers
{
    public class BuildBlockOnEachPendingTx : IBlockProductionTrigger, IDisposable
    {
        private readonly ITxPool _txPool;

        public BuildBlockOnEachPendingTx(ITxPool txPool)
        {
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _txPool.NewPending += TxPoolOnNewPending;
        }

        private void TxPoolOnNewPending(object? sender, TxEventArgs e)
        {
            TriggerBlockProduction?.Invoke(this, new BlockProductionEventArgs());
        }

        public event EventHandler<BlockProductionEventArgs>? TriggerBlockProduction;

        public void Dispose()
        {
            _txPool.NewPending -= TxPoolOnNewPending;
        }
    }
}
