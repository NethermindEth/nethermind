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
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.Facade.Transactions
{
    public class TxNonceTxPoolReserveSealer : TxSealer
    {
        private readonly ITxPool _txPool;

        public TxNonceTxPoolReserveSealer(ITxSigner txSigner, ITimestamper timestamper, ITxPool txPool) : base(txSigner, timestamper, false)
        {
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
        }

        public override void Seal(Transaction tx)
        {
            tx.Nonce = _txPool.ReserveOwnTransactionNonce(tx.SenderAddress);
            base.Seal(tx);
        }
    }
}
