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
        private NChain _provide;

        public VaultTxSender(IVaultConfig vaultConfig)
        {
            _provide = new NChain(
                vaultConfig.Host,
                vaultConfig.Path,
                vaultConfig.Scheme,
                vaultConfig.Token);
        }
        
        public async ValueTask<Keccak> SendTransaction(Transaction tx, TxHandlingOptions txHandlingOptions)
        {
            ProvideTx provideTx = new ProvideTx();
            provideTx.Data = tx.Data.ToHexString();
            provideTx.Description = "From Nethermind with love";
            provideTx.Hash = tx.Hash.ToString();
            provideTx.Signer = tx.SenderAddress.ToString();
            provideTx.To = tx.To.ToString();
            provideTx.Value = (BigInteger)tx.Value;

            ProvideTx createdTx = await _provide.CreateTransaction(provideTx);
            return new Keccak(createdTx.Hash);
        }
    }
}