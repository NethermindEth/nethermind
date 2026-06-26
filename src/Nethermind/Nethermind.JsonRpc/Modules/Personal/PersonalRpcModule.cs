// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security;
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

        public ResultWrapper<Address[]> personal_listAccounts() => ResultWrapper<Address[]>.Success(wallet.GetAccounts());

        public ResultWrapper<bool> personal_lockAccount(Address address)
        {
            bool locked = wallet.LockAccount(address);

            return ResultWrapper<bool>.Success(locked);
        }

        [RequiresSecurityReview("Consider removing any operations that allow to provide passphrase in JSON RPC")]
        public ResultWrapper<bool> personal_unlockAccount(Address address, string passphrase)
        {
            SecureString notSecuredHere = passphrase.Secure();
            bool unlocked = wallet.UnlockAccount(address, notSecuredHere);
            return ResultWrapper<bool>.Success(unlocked);
        }

        [RequiresSecurityReview("Consider removing any operations that allow to provide passphrase in JSON RPC")]
        public ResultWrapper<Address> personal_newAccount(string passphrase)
        {
            SecureString notSecuredHere = passphrase.Secure();
            return ResultWrapper<Address>.Success(wallet.NewAccount(notSecuredHere));
        }

        [RequiresSecurityReview("Consider removing any operations that allow to provide passphrase in JSON RPC")]
        public ResultWrapper<Hash256> personal_sendTransaction(TransactionForRpc transaction, string passphrase) => throw new NotImplementedException();

        public ResultWrapper<Address> personal_ecRecover(byte[] message, byte[] signature)
        {
            if (signature.Length != Signature.Size)
            {
                return ResultWrapper<Address>.Fail($"Invalid signature length: {signature.Length}. Expected {Signature.Size} bytes.", ErrorCodes.InvalidParams);
            }

            Hash256 msgHash = Eip191Hasher.HashMessage(message);
            PublicKey? publicKey = ecdsa.RecoverPublicKey(new Signature(signature), msgHash);
            return publicKey is null
                ? ResultWrapper<Address>.Fail("Could not recover public key from the provided signature.", ErrorCodes.InvalidParams)
                : ResultWrapper<Address>.Success(publicKey.Address);
        }

        [RequiresSecurityReview("Consider removing any operations that allow to provide passphrase in JSON RPC")]
        public ResultWrapper<Signature> personal_sign(byte[] message, Address address, string? passphrase = null)
        {
            bool signed;
            Signature? signature;
            if (passphrase is not null)
            {
                using SecureString securePassphrase = passphrase.Secure();
                signed = wallet.TrySignMessage(message, address, securePassphrase, out signature);
            }
            else
            {
                signed = wallet.TrySignMessage(message, address, out signature);
            }

            return signed
                ? ResultWrapper<Signature>.Success(signature!)
                : ResultWrapper<Signature>.Fail("authentication needed: password or unlock", ErrorCodes.AccountLocked);
        }
    }
}
