// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Crypto
{
    public interface IEthereumEcdsa : IEcdsa
    {
        ulong ChainId { get; }
        Address? RecoverAddress(Transaction tx, bool useSignatureChainId = false);
        Address? RecoverAddress(AuthorizationTuple tuple);
        Address? RecoverAddress(Signature signature, Hash256 message);
        Address? RecoverAddress(Span<byte> signatureBytes, Hash256 message);
        bool Verify(Address sender, Transaction tx);
    }
}
