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
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
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
        private readonly ILogger _logger;

        public GeneratedTxSourceSealer(ITxSource innerSource, ITxSealer txSealer, IStateReader stateReader, ILogManager logManager)
        {
            _innerSource = innerSource ?? throw new ArgumentNullException(nameof(innerSource));
            _txSealer = txSealer ?? throw new ArgumentNullException(nameof(txSealer));
            _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
            _logger = logManager?.GetClassLogger<GeneratedTxSourceSealer>() ?? throw new ArgumentNullException(nameof(logManager));
        }
        
        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit)
        {
            IDictionary<Address, UInt256> nonces = new Dictionary<Address, UInt256>();
            
            return _innerSource.GetTransactions(parent, gasLimit).Select(tx =>
            {
                if (tx is GeneratedTransaction)
                {
                    tx.Nonce = CalculateNonce(tx.SenderAddress, parent.StateRoot, nonces);
                    _txSealer.Seal(tx);
                    if (_logger.IsDebug) _logger.Debug($"Sealed node generated transaction {tx.Hash} from {tx.SenderAddress} to {tx.To} with nonce {tx.Nonce}.");
                }

                return tx;
            });
        }

        private UInt256 CalculateNonce(Address address, Keccak stateRoot, IDictionary<Address, UInt256> nonces)
        {
            if (!nonces.TryGetValue(address, out var nonce))
            {
                nonce = _stateReader.GetNonce(stateRoot, address);
            }
             
            return nonces[address] = nonce;
        }
        
        private readonly int _id = ITxSource.IdCounter;
        public override string ToString() => $"{GetType().Name}_{_id} [ {_innerSource} ]";
    }
}
