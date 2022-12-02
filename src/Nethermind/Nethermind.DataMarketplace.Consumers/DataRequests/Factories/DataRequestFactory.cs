// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
