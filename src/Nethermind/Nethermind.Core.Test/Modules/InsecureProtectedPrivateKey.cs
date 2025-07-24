// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.Core.Test.Modules;

public class InsecureProtectedPrivateKey(PrivateKey privateKey) : IProtectedPrivateKey
{
    public PublicKey PublicKey => privateKey.PublicKey;
    public CompressedPublicKey CompressedPublicKey => privateKey.CompressedPublicKey;
    public PrivateKey Unprotect()
    {
        return privateKey;
    }
}
