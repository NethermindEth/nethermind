// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Crypto
{
    public interface IEthereumEcdsa : IEcdsa
    {
        ulong ChainId { get; }
        Address? RecoverAddress(Signature signature, Hash256 message)
            => RecoverAddress(signature, in message.ValueHash256);

        Address? RecoverAddress(Signature signature, in ValueHash256 message);
        Address? RecoverAddress(Span<byte> signatureBytes, Hash256 message)
            => RecoverAddress(signatureBytes, in message.ValueHash256);
        Address? RecoverAddress(Span<byte> signatureBytes, in ValueHash256 message);
    }
}
