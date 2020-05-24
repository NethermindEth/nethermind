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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.Facade.Transactions
{
    public class TxSealer : ITxSealer
    {
        private readonly IBasicWallet _wallet;
        private readonly ITimestamper _timestamper;
        private readonly int _chainId;
            
        public TxSealer(IBasicWallet wallet, ITimestamper timestamper, int chainId)
        {
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
            _chainId = chainId;
        }

        public virtual void Seal(Transaction tx)
        {
            _wallet.Sign(tx, _chainId);
            tx.Hash = tx.CalculateHash();
            tx.Timestamp = _timestamper.EpochSeconds;
        }
    }
}