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
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Crypto
{
    public class NullEthereumEcdsa : IEthereumEcdsa
    {
        public static NullEthereumEcdsa Instance { get; } = new();

        private NullEthereumEcdsa()
        {
        }

        public Signature Sign(PrivateKey privateKey, Keccak message)
        {
            throw new InvalidOperationException($"{nameof(NullEthereumEcdsa)} does not expect any calls");
        }

        public PublicKey RecoverPublicKey(Signature signature, Keccak message)
        {
            throw new InvalidOperationException($"{nameof(NullEthereumEcdsa)} does not expect any calls");
        }

        public void Sign(PrivateKey privateKey, Transaction tx, bool _)
        {
            throw new InvalidOperationException($"{nameof(NullEthereumEcdsa)} does not expect any calls");
        }

        public Address RecoverAddress(Transaction tx, bool useSignatureChainId = false)
        {
            throw new InvalidOperationException($"{nameof(NullEthereumEcdsa)} does not expect any calls");
        }

        public Address RecoverAddress(Signature signature, Keccak message)
        {
            throw new InvalidOperationException($"{nameof(NullEthereumEcdsa)} does not expect any calls");
        }

        public Address RecoverAddress(Span<byte> signatureBytes, Keccak message)
        {
            throw new InvalidOperationException($"{nameof(NullEthereumEcdsa)} does not expect any calls");
        }

        public bool Verify(Address sender, Transaction tx)
        {
            throw new InvalidOperationException($"{nameof(NullEthereumEcdsa)} does not expect any calls");
        }
    }
}
