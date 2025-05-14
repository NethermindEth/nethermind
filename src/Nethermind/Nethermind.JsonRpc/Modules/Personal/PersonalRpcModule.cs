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
    public class PersonalRpcModule : IPersonalRpcModule
    {
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
        public ResultWrapper<Hash256> personal_sendTransaction(TransactionForRpc transaction, string passphrase)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Address> personal_ecRecover(byte[] message, byte[] signature)
        {
            message = ToEthSignedMessage(message);
            Hash256 msgHash = Keccak.Compute(message);
            PublicKey publicKey = _ecdsa.RecoverPublicKey(new Signature(signature), msgHash);
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
            if (!_wallet.IsUnlocked(address))
            {
                if (passphrase is not null)
                {
                    var notSecuredHere = passphrase.Secure();
                    _wallet.UnlockAccount(address, notSecuredHere);
                }
            }

            message = ToEthSignedMessage(message);
            return ResultWrapper<string>.Success(_wallet.Sign(Keccak.Compute(message), address).ToString());
        }
    }
}
