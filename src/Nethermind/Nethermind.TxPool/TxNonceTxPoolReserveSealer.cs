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
using System.Threading.Tasks;
using Nethermind.Core;

namespace Nethermind.TxPool
{
    // TODO: this should be nonce reserving tx sender, not sealer?
    public class NonceReservingTxSealer : TxSealer
    {
        private readonly ITxPool _txPool;

        public NonceReservingTxSealer(ITxSigner txSigner, ITimestamper timestamper, ITxPool txPool)
            : base(txSigner, timestamper)
        {
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
        }

        public override ValueTask Seal(Transaction tx, TxHandlingOptions txHandlingOptions)
        {
            bool manageNonce = (txHandlingOptions & TxHandlingOptions.ManagedNonce) == TxHandlingOptions.ManagedNonce;
            if (manageNonce)
            {
                tx.Nonce = _txPool.ReserveOwnTransactionNonce(tx.SenderAddress);
                txHandlingOptions |= TxHandlingOptions.AllowReplacingSignature;
            }

            return base.Seal(tx, txHandlingOptions);
        }
    }
}
