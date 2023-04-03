// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Crypto
{
    public interface IEthereumEcdsa : IEcdsa
    {
        void Sign(PrivateKey privateKey, Transaction tx, bool isEip155Enabled = true);
        Address? RecoverAddress(Transaction tx, bool useSignatureChainId = false);
        Address? RecoverAddress(Signature signature, Keccak message);
        Address? RecoverAddress(Span<byte> signatureBytes, Keccak message);
        bool Verify(Address sender, Transaction tx);
    }
}
