// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.KeyStore;
using Nethermind.Wallet;

namespace Nethermind.JsonRpc.Modules.Personal
{
    public class PersonalRpcModule(IEcdsa ecdsa, IWallet wallet, IKeyStore keyStore) : IPersonalRpcModule
    {
        [RequiresSecurityReview("Consider removing any operations that allow to provide passphrase in JSON RPC")]
        public ResultWrapper<Address> personal_importRawKey(byte[] keyData, string passphrase)
        {
            PrivateKey privateKey = new(keyData);
            keyStore.StoreKey(privateKey, passphrase.Secure());
            return ResultWrapper<Address>.Success(privateKey.Address);
        }

        public ResultWrapper<Address[]> personal_listAccounts()
        {
            return ResultWrapper<Address[]>.Success(wallet.GetAccounts());
        }

        public ResultWrapper<bool> personal_lockAccount(Address address)
        {
            var locked = wallet.LockAccount(address);

            return ResultWrapper<bool>.Success(locked);
        }

        [RequiresSecurityReview("Consider removing any operations that allow to provide passphrase in JSON RPC")]
        public ResultWrapper<bool> personal_unlockAccount(Address address, string passphrase)
        {
            var notSecuredHere = passphrase.Secure();
            var unlocked = wallet.UnlockAccount(address, notSecuredHere);
            return ResultWrapper<bool>.Success(unlocked);
        }

        [RequiresSecurityReview("Consider removing any operations that allow to provide passphrase in JSON RPC")]
        public ResultWrapper<Address> personal_newAccount(string passphrase)
        {
            var notSecuredHere = passphrase.Secure();
            return ResultWrapper<Address>.Success(wallet.NewAccount(notSecuredHere));
        }

        [RequiresSecurityReview("Consider removing any operations that allow to provide passphrase in JSON RPC")]
        public ResultWrapper<Hash256> personal_sendTransaction(TransactionForRpc transaction, string passphrase)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Address> personal_ecRecover(byte[] message, byte[] signature)
        {
            message = ToEthSignedMessage(message);
            Hash256 msgHash = Keccak.Compute(message);
            PublicKey publicKey = ecdsa.RecoverPublicKey(new Signature(signature), msgHash);
            return ResultWrapper<Address>.Success(publicKey.Address);
        }

        private static byte[] ToEthSignedMessage(byte[] message)
        {
            string messageString = $"\u0019Ethereum Signed Message:\n{message.Length}{UTF8Encoding.UTF8.GetString(message)}";
            message = UTF8Encoding.UTF8.GetBytes(messageString);
            return message;
        }

        [RequiresSecurityReview("Consider removing any operations that allow to provide passphrase in JSON RPC")]
        public ResultWrapper<string> personal_sign(byte[] message, Address address, string passphrase = null)
        {
            if (!wallet.IsUnlocked(address))
            {
                if (passphrase is not null)
                {
                    var notSecuredHere = passphrase.Secure();
                    wallet.UnlockAccount(address, notSecuredHere);
                }
            }

            message = ToEthSignedMessage(message);
            return ResultWrapper<string>.Success(wallet.Sign(Keccak.Compute(message), address).ToString());
        }
    }
}
