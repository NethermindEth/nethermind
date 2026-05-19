// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus;

public interface IHeaderSigner : ISigner
{
    bool CanSignHeader { get; }
    bool TrySign(BlockHeader header, [NotNullWhen(true)] out Signature signature);

    Signature Sign(BlockHeader header)
    {
        if (!TrySign(header, out Signature signature))
            throw new InvalidOperationException($"Header signer {Address} cannot sign header {header.Number}.");
        return signature;
    }
}
