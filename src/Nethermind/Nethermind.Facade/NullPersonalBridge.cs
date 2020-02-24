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

using System;
using System.Security;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Facade
{
    public class NullPersonalBridge : IPersonalBridge
    {
        private NullPersonalBridge()
        {
        }

        public static IPersonalBridge Instance { get; } = new NullPersonalBridge();

        public Address[] ListAccounts()
        {
            return Array.Empty<Address>();
        }

        public Address NewAccount(SecureString passphrase)
        {
            throw new NotSupportedException();
        }

        public bool UnlockAccount(Address address, SecureString notSecuredHere)
        {
            return false;
        }

        public bool LockAccount(Address address)
        {
            return false;
        }

        public bool IsUnlocked(Address address)
        {
            return false;
        }

        public Address EcRecover(byte[] message, Signature signature)
        {
            throw new NotSupportedException();
        }

        public Signature Sign(byte[] message, Address address)
        {
            throw new NotSupportedException();
        }
    }
}