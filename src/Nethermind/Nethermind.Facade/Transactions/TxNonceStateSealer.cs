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
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.Facade.Transactions
{
    public class TxNonceStateSealer : TxSealer
    {
        private readonly IStateReader _stateReader;
        private readonly Keccak _state;

        public TxNonceStateSealer(IBasicWallet wallet, ITimestamper timestamper, int chainId, IStateReader stateReader, Keccak state) : base(wallet, timestamper, chainId)
        {
            _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
            _state = state;
        }

        public override void Seal(Transaction tx)
        {
            tx.Nonce = _stateReader.GetNonce(_state, tx.SenderAddress);
            base.Seal(tx);
        }
    }

    public class TxNonceStateSealerFactory : IStateTxSealerFactory
    {
        private readonly IBasicWallet _wallet;
        private readonly ITimestamper _timestamper;
        private readonly int _chainId;
        private readonly IStateReader _stateReader;

        public TxNonceStateSealerFactory(IBasicWallet wallet, ITimestamper timestamper, int chainId, IStateReader stateReader)
        {
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
            _chainId = chainId;
            _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
        }
        
        public ITxSealer CreateTxSealerForState(Keccak state) => new TxNonceStateSealer(_wallet, _timestamper, _chainId, _stateReader, state);
    }
}