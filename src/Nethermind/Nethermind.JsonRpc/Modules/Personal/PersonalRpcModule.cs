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

using System;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.JsonRpc.Data;
using Nethermind.KeyStore;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Wallet;

namespace Nethermind.JsonRpc.Modules.Personal
{
    public class PersonalRpcModule : IPersonalRpcModule
    {
        private Encoding _messageEncoding = Encoding.UTF8;
        private readonly IEcdsa _ecdsa;
        private readonly IWallet _wallet;
        private readonly IKeyStore _keyStore;

        public PersonalRpcModule(IEcdsa ecdsa, IWallet wallet, IKeyStore keyStore)
        {
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _keyStore = keyStore;
        }

         [RequiresSecurityReview("Consider removing any operations that allow to provide passphrase in JSON RPC")]
         public ResultWrapper<Address> personal_importRawKey(byte[] keyData, string passphrase)
         {
             PrivateKey privateKey = new(keyData);
             _keyStore.StoreKey(privateKey, passphrase.Secure());
             return ResultWrapper<Address>.Success(privateKey.Address);
         }

        public ResultWrapper<Address[]> personal_listAccounts()
        {
            return ResultWrapper<Address[]>.Success(_wallet.GetAccounts());
        }

        public ResultWrapper<bool> personal_lockAccount(Address address)
        {
            var locked = _wallet.LockAccount(address);

            return ResultWrapper<bool>.Success(locked);
        }

        [RequiresSecurityReview("Consider removing any operations that allow to provide passphrase in JSON RPC")]
        public ResultWrapper<bool> personal_unlockAccount(Address address, string passphrase)
        {
            var notSecuredHere = passphrase.Secure();
            var unlocked = _wallet.UnlockAccount(address, notSecuredHere);
            return ResultWrapper<bool>.Success(unlocked);
        }

        [RequiresSecurityReview("Consider removing any operations that allow to provide passphrase in JSON RPC")]
        public ResultWrapper<Address> personal_newAccount(string passphrase)
        {
            var notSecuredHere = passphrase.Secure();
            return ResultWrapper<Address>.Success(_wallet.NewAccount(notSecuredHere));
        }
        
        [RequiresSecurityReview("Consider removing any operations that allow to provide passphrase in JSON RPC")]
        public ResultWrapper<Keccak> personal_sendTransaction(TransactionForRpc transaction, string passphrase)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Address> personal_ecRecover(byte[] message, byte[] signature)
        {
            message = ToEthSignedMessage(message);
            Keccak msgHash = Keccak.Compute(message);
            PublicKey publicKey = _ecdsa.RecoverPublicKey(new Signature(signature), msgHash);
            return ResultWrapper<Address>.Success(publicKey.Address);
        }

        private static byte[] ToEthSignedMessage(byte[] message)
        {
            string messageString = $"\\x19Ethereum Signed Message:\\n{message.Length}{UTF8Encoding.UTF8.GetString(message)}";
            message = UTF8Encoding.UTF8.GetBytes(messageString);
            return message;
        }

        [RequiresSecurityReview("Consider removing any operations that allow to provide passphrase in JSON RPC")]
        public ResultWrapper<byte[]> personal_sign(byte[] message, Address address, string passphrase = null)
        {
            if (!_wallet.IsUnlocked(address))
            {
                if (passphrase != null)
                {
                    var notSecuredHere = passphrase.Secure();                    
                    _wallet.UnlockAccount(address, notSecuredHere);
                }
            }
            
            message = ToEthSignedMessage(message);
            return ResultWrapper<byte[]>.Success(_wallet.Sign(Keccak.Compute(message), address).Bytes);
        }
    }
}
