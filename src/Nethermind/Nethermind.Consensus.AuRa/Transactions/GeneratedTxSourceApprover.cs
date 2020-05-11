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
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.State;
using Nethermind.Wallet;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class GeneratedTxSourceApprover : ITxSource
    {
        private readonly ITxSource _innerSource;
        private readonly IBasicWallet _wallet;
        private readonly ITimestamper _timestamper;
        private readonly IStateReader _stateReader;
        private readonly int _chainId;

        public GeneratedTxSourceApprover(ITxSource innerSource, IBasicWallet wallet, ITimestamper timestamper, IStateReader stateReader, int chainId)
        {
            _innerSource = innerSource ??  throw new ArgumentNullException(nameof(innerSource));
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
            _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
            _chainId = chainId;
        }
        
        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit) =>
            _innerSource.GetTransactions(parent, gasLimit).Select(tx =>
            {
                if (tx is GeneratedTransaction)
                {
                    ApproveTx(parent, tx);
                }

                return tx;
            });

        private void ApproveTx(BlockHeader parent, Transaction tx)
        {
            tx.Nonce = _stateReader.GetNonce(parent.StateRoot, tx.SenderAddress);
            _wallet.Sign(tx, _chainId);
            tx.Hash = tx.CalculateHash();
            tx.Timestamp = _timestamper.EpochSeconds;
        }

    }
}