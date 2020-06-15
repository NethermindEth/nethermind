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
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.Consensus.AuRa.Transactions
{
    public class GeneratedTxSourceSealer : ITxSource
    {
        private readonly ITxSource _innerSource;
        private readonly ITxSealer _txSealer;
        private readonly IStateReader _stateReader;
        private readonly ISigner _signer;

        public GeneratedTxSourceSealer(ITxSource innerSource, ITxSealer txSealer, IStateReader stateReader, ISigner signer)
        {
            _innerSource = innerSource ?? throw new ArgumentNullException(nameof(innerSource));
            _txSealer = txSealer ?? throw new ArgumentNullException(nameof(txSealer));
            _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
            _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        }
        
        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit)
        {
            var nodeNonce = _stateReader.GetNonce(parent.StateRoot, _signer.Address);
            
            return _innerSource.GetTransactions(parent, gasLimit).Select(tx =>
            {
                if (tx is GeneratedTransaction)
                {
                    tx.Nonce = ++nodeNonce;
                    _txSealer.Seal(tx);
                }

                return tx;
            });
        }
    }
}
