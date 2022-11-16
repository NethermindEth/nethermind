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
using Nethermind.Core.Crypto;

namespace Nethermind.TxPool
{
    public class TxPoolSender : ITxSender
    {
        private readonly ITxPool _txPool;
        private readonly ITxSealer _sealer;

        public TxPoolSender(ITxPool txPool, ITxSealer sealer)
        {
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _sealer = sealer ?? throw new ArgumentNullException(nameof(sealer));
        }

        public ValueTask<(Keccak, AcceptTxResult?)> SendTransaction(Transaction tx, TxHandlingOptions txHandlingOptions)
        {
            _sealer.Seal(tx, txHandlingOptions);
            AcceptTxResult result = _txPool.SubmitTx(tx, txHandlingOptions);
            return new ValueTask<(Keccak, AcceptTxResult?)>((tx.Hash!, result)); // The sealer calculates the hash
        }
    }
}
