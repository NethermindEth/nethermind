/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Linq;
using System.Security;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Wallet;

namespace Nethermind.Facade
{
    public class PersonalBridge : IPersonalBridge
    {
        private readonly IEthereumEcdsa _ecdsa;
        private readonly IWallet _wallet;

        public PersonalBridge(IEthereumEcdsa ecdsa, IWallet wallet)
        {
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
        }
        
        public Address[] ListAccounts()
        {
            return _wallet.GetAccounts().ToArray();
        }

        public Address NewAccount(SecureString passphrase)
        {
            return _wallet.NewAccount(passphrase);
        }

        public bool UnlockAccount(Address address, SecureString notSecuredHere)
        {
            return _wallet.UnlockAccount(address, notSecuredHere);
        }

        public bool LockAccount(Address address)
        {
            return _wallet.LockAccount(address);
        }

        public bool IsUnlocked(Address address)
        {
            return _wallet.IsUnlocked(address);
        }

        public Address EcRecover(byte[] message, Signature signature)
        {
            throw new NotImplementedException();
//            return _ecdsa.RecoverAddress(signature, Keccak.Compute(message));
        }

        public Signature Sign(byte[] message, Address address)
        {
            throw new NotImplementedException();
//            return _wallet.Sign(Keccak.Compute(message), address);
        }
    }
}