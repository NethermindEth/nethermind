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
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;

namespace Nethermind.Vault
{
    public class VaultTxSigner : ITxSigner
    {
        private readonly IVaultWallet _vaultWallet;
        private readonly ulong _chainId;

        public VaultTxSigner(IVaultWallet vaultWallet, ulong chainId)
        {
            _vaultWallet = vaultWallet ?? throw new ArgumentNullException(nameof(vaultWallet));
            _chainId = chainId;
        }
        
        public async ValueTask Sign(Transaction tx)
        {
            Keccak hash = Keccak.Compute(Rlp.Encode(tx, true, true, _chainId).Bytes);
            tx.Signature = await _vaultWallet.Sign(tx.SenderAddress, hash);
            tx.Signature!.V = tx.Signature.V + 8 + 2 * _chainId;
        }
    }
}
