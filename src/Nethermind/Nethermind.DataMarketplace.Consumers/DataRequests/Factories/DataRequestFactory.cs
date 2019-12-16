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

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Wallet;

namespace Nethermind.DataMarketplace.Consumers.DataRequests.Factories
{
    public class DataRequestFactory : IDataRequestFactory
    {
        private readonly IWallet _wallet;
        private readonly Keccak _nodeHash;

        public DataRequestFactory(IWallet wallet, PublicKey nodePublicKey)
        {
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _nodeHash = nodePublicKey is null
                ? throw new ArgumentNullException(nameof(nodePublicKey))
                : Keccak.Compute(nodePublicKey.Bytes);
        }

        public DataRequest Create(Deposit deposit, Keccak dataAssetId, Address provider, Address consumer,
            byte[] pepper)
        {
            if (!_wallet.IsUnlocked(consumer))
            {
                throw new InvalidOperationException($"Cannot create a data request, locked account: {consumer}");
            }

            return new DataRequest(dataAssetId, deposit.Units, deposit.Value, deposit.ExpiryTime, pepper,
                provider, consumer, _wallet.Sign(_nodeHash, consumer));
        }
    }
}