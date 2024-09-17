// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using Nethermind.Core.Crypto;
using Nethermind.Network.Discovery.Kademlia;

namespace Nethermind.Network.Discovery.Portal;

public class ContentKeyHashProvider : IContentHashProvider<byte[]>
{
    public static ContentKeyHashProvider Instance = new ContentKeyHashProvider();
    private ContentKeyHashProvider()
    {
    }

    public ValueHash256 GetHash(byte[] key)
    {
        using SHA256 sha256 = SHA256.Create();

        ValueHash256 asValueHash256 = new ValueHash256();
        sha256.TryComputeHash(key, asValueHash256.BytesAsSpan, out _);
        return asValueHash256;
    }
}
