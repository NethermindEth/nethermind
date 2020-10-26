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
using System.Numerics;
using System.Threading.Tasks;
using Ipfs;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.TxPool;
using Nethermind.Vault.Config;
using provide;
using ProvideTx = provide.Model.NChain.Transaction;

namespace Nethermind.Vault
{
    public class VaultTxSender : ITxSender
    {
        private static Dictionary<int, Guid> _networkIdMapping = new Dictionary<int, Guid>
        {
            // Bitcoin mainnet: 248fa53c-3df4-41af-ab6b-b78f94d6c25c 	 mainnet 
            {1, new Guid("deca2436-21ba-4ff5-b225-ad1b0b2f5c59")}, // Ethereum mainnet
            {3, new Guid("66d44f30-9092-4182-a3c4-bc02736d6ae5")}, // Ethereum Ropsten testnet
            {4, new Guid("07102258-5e49-480e-86af-6d0c3260827d")}, // Ethereum Rinkeby testnet
            {5, new Guid("1b16996e-3595-4985-816c-043345d22f8c")}, // Ethereum Görli Testnet
            {42, new Guid("8d31bf48-df6b-4a71-9d7c-3cb291111e27")} // Ethereum Kovan testnet 
        };

        private readonly Guid? _networkId;
        private readonly ITxSigner _txSigner;

        private NChain _provide;

        public VaultTxSender(ITxSigner txSigner, IVaultConfig vaultConfig, int chainId)
        {
            _txSigner = txSigner;
            if (_networkIdMapping.ContainsKey(chainId)) _networkId = _networkIdMapping[chainId];

            _provide = new NChain(
                vaultConfig.Host,
                vaultConfig.Path,
                vaultConfig.Scheme,
                vaultConfig.Token);
        }

        public async ValueTask<Keccak> SendTransaction(Transaction tx, TxHandlingOptions txHandlingOptions)
        {
            ProvideTx provideTx = new ProvideTx();
            provideTx.Data = (tx.Data ?? tx.Init).ToHexString();
            provideTx.Description = "From Nethermind with love";
            provideTx.Hash = tx.Hash.ToString();
            provideTx.Signer = tx.SenderAddress.ToString();
            provideTx.NetworkId = _networkId;
            provideTx.To = tx.To.ToString();
            provideTx.Value = (BigInteger) tx.Value;
            provideTx.Params = new Dictionary<string, object>
            {
                {"subsidize", true}
            };

            // this should happen after we set the GasPrice
            _txSigner.Seal(tx);
            ProvideTx createdTx = await _provide.CreateTransaction(provideTx);
            return new Keccak(createdTx.Hash);
        }
    }
}
