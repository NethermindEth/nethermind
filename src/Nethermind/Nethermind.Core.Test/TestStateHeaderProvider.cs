// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Core.Test;

/// <summary>Header provider stub serving the headers it was given, matched on <see cref="BlockHeader.ParentHash"/>.</summary>
public sealed class TestStateHeaderProvider : IStateHeaderProvider
{
    private readonly Dictionary<Hash256, BlockHeader> _headers = [];

    /// <summary>The single known header; setting it replaces everything <see cref="Add"/> registered.</summary>
    public BlockHeader? Parent
    {
        get => _parent;
        set
        {
            _parent = value;
            _headers.Clear();
            if (value is not null) Add(value);
        }
    }

    private BlockHeader? _parent;

    public int LookupCalls { get; private set; }
    public bool ThrowOnLookup { get; set; }

    /// <summary>Registers one more header of the chain, so a multi-block test can resolve each block's own parent.</summary>
    public BlockHeader Add(BlockHeader header)
    {
        _headers[header.Hash ?? throw new ArgumentException("A known header must have a hash.", nameof(header))] = header;
        return header;
    }

    public BlockHeader? FindParentHeader(BlockHeader target)
    {
        LookupCalls++;
        if (ThrowOnLookup) throw new InvalidOperationException("Parent lookup must not be called for genesis.");
        return target.ParentHash is not null && _headers.TryGetValue(target.ParentHash, out BlockHeader? parent) ? parent : null;
    }

    public ulong FinalizedBlockNumber => 0;

    public BlockHeader? GetFinalizedHeader(ulong blockNumber) => null;
}
