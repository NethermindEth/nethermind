//  Copyright (c) 2021 Demerzel Solutions Limited
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
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Wallet
{
    public static class WalletExtensions
    {
        public static void SetupTestAccounts(this IWallet wallet, byte count)
        {
            byte[] keySeed = new byte[32];
            keySeed[31] = 1;
            for (int i = 0; i < count; i++)
            {
                PrivateKey key = new PrivateKey(keySeed);
                SecureString secureString = string.Empty.Secure();
                if (wallet.GetAccounts().All(a => a != key.Address))
                {
                    wallet.Import(keySeed, secureString);
                }

                wallet.UnlockAccount(key.Address, secureString, TimeSpan.FromHours(24));
                keySeed[31]++;
            }
        }
        
        public static void Sign(this IWallet @this, Transaction tx, ulong chainId)
        {
            Keccak hash = Keccak.Compute(Rlp.Encode(tx, true, true, chainId).Bytes);
            tx.Signature = @this.Sign(hash, tx.SenderAddress);
            if (tx.Signature is null)
            {
                throw new CryptographicException($"Failed to sign tx {tx.Hash} using the {tx.SenderAddress} address.");
            }
            
            tx.Signature.V = tx.Signature.V + 8 + 2 * chainId;
        }
    }
}
