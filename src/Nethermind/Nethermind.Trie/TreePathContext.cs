// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie;

public readonly struct EmptyContext : INodeContext<EmptyContext>
{
    public EmptyContext Add(ReadOnlySpan<byte> nibblePath) => this;
    public EmptyContext Add(byte nibble) => this;
    public EmptyContext AddStorage(in ValueHash256 storage) => this;
}

public struct TreePathContext : INodeContext<TreePathContext>
{
    public TreePath Path = TreePath.Empty;

    public TreePathContext()
    {
    }

    public TreePathContext Add(ReadOnlySpan<byte> nibblePath) => new()
    {
        Path = Path.Append(nibblePath)
    };

    public TreePathContext Add(byte nibble) => new()
    {
        Path = Path.Append(nibble)
    };

    public readonly TreePathContext AddStorage(in ValueHash256 storage) => new();
}

public interface ITreePathContextWithStorage
{
    TreePath Path { get; }
    Hash256? Storage { get; }
}

public readonly struct TreePathContextWithStorage : ITreePathContextWithStorage, INodeContext<TreePathContextWithStorage>
{
    public TreePath Path { get; init; } = TreePath.Empty;
    // Not using ValueHash as value is shared with many context.
    public Hash256? Storage { get; init; }

    public TreePathContextWithStorage()
    {
    }

    public TreePathContextWithStorage Add(ReadOnlySpan<byte> nibblePath) => new()
    {
        Path = Path.Append(nibblePath),
        Storage = Storage
    };

    public TreePathContextWithStorage Add(byte nibble) => new()
    {
        Path = Path.Append(nibble),
        Storage = Storage
    };

    public readonly TreePathContextWithStorage AddStorage(in ValueHash256 storage) => new()
    {
        Path = TreePath.Empty,
        Storage = Path.Path.ToCommitment(),
    };
}

public interface INodeContext<out TNodeContext>
    where TNodeContext : struct, INodeContext<TNodeContext>
{
    TNodeContext Add(ReadOnlySpan<byte> nibblePath);

    TNodeContext Add(byte nibble);
    TNodeContext AddStorage(in ValueHash256 storage);
}
