// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Crypto;

public interface IProtectedPrivateKey
{
    public const string NodeKey = "NodeKey";

    PublicKey PublicKey { get; }
    CompressedPublicKey CompressedPublicKey { get; }
    PrivateKey Unprotect();
}
