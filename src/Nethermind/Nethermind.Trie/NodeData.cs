// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core;

namespace Nethermind.Trie;

public interface INodeData
{
    public NodeType NodeType { get; }
    public INodeData Clone();
    public int Length { get; }
    public ref object this[int index] { get; }
    public int Size { get; }
}

interface INodeWithKey : INodeData
{
    public byte[] Key { get; set; }
}

public class BranchData : INodeData
{
    public NodeType NodeType => NodeType.Branch;
    public int Length => TrieNode.BranchesCount;
    public int Size => MemorySizes.RefSize * TrieNode.BranchesCount;

    private BranchArray _branches;

    public BranchData() { }

    private BranchData(in BranchArray branches) => _branches = branches;

    public ref readonly BranchArray Branches => ref _branches;
    public ref object this[int index]
    {
        get
        {
            return ref _branches[index];
        }
    }

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
    public int Size => MemorySizes.RefSize + MemorySizes.RefSize +
        (_key is not null ? (int)MemorySizes.Align(_key.Length + MemorySizes.ArrayOverhead) : 0);
    public int Length => 2;

    public byte[]? _key;
    public object? _value;
    public byte[] Key { get => _key; set => _key = value; }
    public object? Value { get => _value; set => _value = value; }
    public ref object this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Length);

            return ref _value;
        }
    }

    public ExtensionData() { }

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
    public int Size => MemorySizes.RefSize + MemorySizes.RefSize + MemorySizes.RefSize +
         (Key is not null ? (int)MemorySizes.Align(Key.Length + MemorySizes.ArrayOverhead) : 0) +
         (_value.IsNotNull ? (int)MemorySizes.Align(_value.Length + MemorySizes.ArrayOverhead) : 0);
    private readonly CappedArray<byte> _value;

    public byte[] Key { get; set; }
    public ref readonly CappedArray<byte> Value => ref _value;
    public TrieNode? StorageRoot { get; set; }

    public LeafData() { }

    internal LeafData(byte[] key, in CappedArray<byte> value)
    {
        Key = key;
        _value = value;
    }

    private LeafData(byte[] key, in CappedArray<byte> value, TrieNode? storageRoot)
    {
        Key = key;
        _value = value;
        StorageRoot = storageRoot;
    }
    public ref object this[int index]
    {
        get
        {
            throw new IndexOutOfRangeException();
        }
    }

    INodeData INodeData.Clone() => new LeafData(Key, in _value);
    public LeafData CloneWithNewValue(in CappedArray<byte> value) => new LeafData(Key, in value, StorageRoot);
}
