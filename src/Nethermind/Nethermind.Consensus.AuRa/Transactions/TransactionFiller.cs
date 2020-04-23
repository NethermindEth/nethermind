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
using Nethermind.Crypto;
using Nethermind.State;
using Nethermind.Wallet;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class TransactionFiller : ITransactionFiller
    {
        private readonly IBasicWallet _wallet;
        private readonly ITimestamper _timestamper;
        private readonly IStateReader _stateReader;
        private readonly int _chainId;

        public TransactionFiller(IBasicWallet wallet, ITimestamper timestamper, IStateReader stateReader, int chainId)
        {
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
            _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
            _chainId = chainId;
        }
        
        public void Fill(BlockHeader parent, Transaction tx)
        {
            tx.Nonce = _stateReader.GetNonce(parent.StateRoot, tx.SenderAddress);
            _wallet.Sign(tx, _chainId);
            tx.Hash = tx.CalculateHash();
            tx.Timestamp = _timestamper.EpochSeconds;
        }
    }
}