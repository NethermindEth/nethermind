// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.TxPool;

namespace Nethermind.Consensus
{
    public interface ISigner : ITxSigner
    {
        // TODO: this breaks the encapsulation of the key inside the signer, would like to see this removed
        PrivateKey? Key { get; }
        Address Address { get; }
        bool TrySign(in ValueHash256 message, [NotNullWhen(true)] out Signature signature);
        bool CanSign { get; }

        Signature Sign(in ValueHash256 message)
        {
            if (!TrySign(in message, out Signature signature))
                throw new InvalidOperationException($"Signer {Address} cannot sign — no key configured or signing was rejected.");
            return signature;
        }
    }
}
