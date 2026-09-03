// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>An owned encoded canonical node and its encoded location in the tree.</summary>
public sealed class PbtEncodedNode
{
    private readonly byte[] _locatorEncoding;
    private readonly byte[] _nodeEncoding;

    internal PbtEncodedNode(ReadOnlySpan<byte> locatorEncoding, ReadOnlySpan<byte> nodeEncoding)
    {
        _locatorEncoding = locatorEncoding.ToArray();
        _nodeEncoding = nodeEncoding.ToArray();
    }

    /// <summary>Gets the canonical encoded node locator.</summary>
    public ReadOnlyMemory<byte> LocatorEncoding => _locatorEncoding;

    /// <summary>Gets the canonical encoded node.</summary>
    public ReadOnlyMemory<byte> NodeEncoding => _nodeEncoding;
}

/// <summary>The root and encoded nodes produced by a canonical tree rebuild.</summary>
public sealed class PbtCanonicalBuildResult
{
    internal PbtCanonicalBuildResult(ValueHash256 rootHash, IReadOnlyList<PbtEncodedNode> nodes)
    {
        RootHash = rootHash;
        Nodes = nodes;
    }

    /// <summary>Gets the rebuilt canonical root hash.</summary>
    public ValueHash256 RootHash { get; }

    /// <summary>Gets the encoded nodes ordered by their encoded locators.</summary>
    public IReadOnlyList<PbtEncodedNode> Nodes { get; }
}
