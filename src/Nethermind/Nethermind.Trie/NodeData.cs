// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;

namespace Nethermind.Trie;

public interface INodeData
{
    public NodeType NodeType { get; }
    public INodeData Clone();
    public int Length { get; }
    public ref object this[int index] { get; }
    public int MemorySize { get; }
}

interface INodeWithKey : INodeData
{
    public byte[] Key { get; set; }
}

public class BranchData : INodeData
{
    public NodeType NodeType => NodeType.Branch;
    public int Length => TrieNode.BranchesCount;
    public int MemorySize => MemorySizes.RefSize * TrieNode.BranchesCount;

    private BranchArray _branches;

    public BranchData()
    {
    }

    private BranchData(in BranchArray branches) => _branches = branches;

    public ref readonly BranchArray Branches => ref _branches;
    public ref object this[int index] => ref _branches[index];

    INodeData INodeData.Clone() => new BranchData(in _branches);

    [InlineArray(Length)]
    public struct BranchArray
    {
        public const int Length = TrieNode.BranchesCount;
        private object? _element0;
    }
}

public class ExtensionData : INodeWithKey
{
    public NodeType NodeType => NodeType.Extension;

    public int MemorySize => MemorySizes.RefSize + MemorySizes.RefSize +
                             (_key is not null ? (int)MemorySizes.Align(_key.Length + MemorySizes.ArrayOverhead) : 0);

    public int Length => 2;

    public byte[]? _key;
    public object? _value;

    public byte[] Key
    {
        get => _key;
        set => _key = value;
    }

    public object? Value
    {
        get => _value;
        set => _value = value;
    }

    public ref object this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Length)
            {
                ThrowArgumentOutOfRangeException(index);
            }

            return ref _value;

            [DoesNotReturn, StackTraceHidden]
            static void ThrowArgumentOutOfRangeException(int index)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, $"{index} is not 0 or 1");
            }
        }
    }

    public ExtensionData()
    {
    }

    internal ExtensionData(byte[] key)
    {
        Key = key;
    }

    internal ExtensionData(byte[] key, TrieNode value)
    {
        Key = key;
        Value = value;
    }

    private ExtensionData(byte[] key, object? value)
    {
        Key = key;
        Value = value;
    }

    INodeData INodeData.Clone() => new ExtensionData(Key, Value);
}

public class LeafData : INodeWithKey
{
    public NodeType NodeType => NodeType.Leaf;
    public int Length => 0;

    public int MemorySize => MemorySizes.RefSize + // StorageRoot
                             MemorySizes.RefSize + // RefSize for Key, then the key itself
                             (Key is not null ? (int)MemorySizes.Align(Key.Length + MemorySizes.ArrayOverhead) : 0) +
                             _value.MemorySize; // value

    private readonly SpanSource _value;

    public byte[] Key { get; set; }
    public SpanSource Value => _value;
    public TrieNode? StorageRoot { get; set; }

    public LeafData()
    {
        _value = SpanSource.Empty;
    }

    internal LeafData(byte[] key, SpanSource value)
    {
        Key = key;
        _value = value;
    }

    private LeafData(byte[] key, SpanSource value, TrieNode? storageRoot)
    {
        Key = key;
        _value = value;
        StorageRoot = storageRoot;
    }

    public ref object this[int index] => throw new IndexOutOfRangeException();

    INodeData INodeData.Clone() => new LeafData(Key, _value);

    public LeafData CloneWithNewValue(SpanSource value) => new(Key, value, StorageRoot);
}
