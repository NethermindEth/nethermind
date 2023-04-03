// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Producers
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
