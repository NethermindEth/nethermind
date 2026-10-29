// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;
using Nethermind.Core.Collections;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Per-account changes of a <see cref="GeneratedBlockAccessList"/>, ordered by index.
/// </summary>
public class GeneratedAccountChanges(Address address) : IComparable<GeneratedAccountChanges>
{
    [JsonConverter(typeof(AddressConverter))]
    public Address Address { get; } = address;

    public int CompareTo(GeneratedAccountChanges? other) => other is null ? 1 : Address.CompareTo(other.Address);

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<BalanceChange> BalanceChanges { get; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<NonceChange> NonceChanges { get; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<CodeChange> CodeChanges { get; } = [];

    private readonly Dictionary<UInt256, GeneratedSlotChanges> _storageChanges = new(UInt256Comparer.GetOptimized());
    private readonly HashSet<UInt256> _storageReads = new(UInt256Comparer.GetOptimized());

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyCollection<GeneratedSlotChanges> StorageChanges => _storageChanges.Values;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlySet<UInt256> StorageReads => _storageReads;

    /// <summary>
    /// Slot-key-sorted snapshot; pooled, dispose after use.
    /// </summary>
    public ArrayPoolListRef<GeneratedSlotChanges> GetSortedStorageChanges()
    {
        ArrayPoolListRef<GeneratedSlotChanges> result = new(_storageChanges.Count);
        foreach (GeneratedSlotChanges sc in _storageChanges.Values) result.Add(sc);
        result.AsSpan().Sort(GenericComparer.GetOptimized<GeneratedSlotChanges>());
        return result;
    }

    /// <summary>
    /// Slot-key-sorted snapshot; pooled, dispose after use.
    /// </summary>
    public ArrayPoolListRef<UInt256> GetSortedStorageReads()
    {
        ArrayPoolListRef<UInt256> result = new(_storageReads.Count);
        foreach (UInt256 r in _storageReads) result.Add(r);
        result.AsSpan().Sort(GenericComparer.GetOptimized<UInt256>());
        return result;
    }

    public GeneratedSlotChanges GetOrAddSlotChanges(UInt256 key)
    {
        if (!_storageChanges.TryGetValue(key, out GeneratedSlotChanges? existing))
        {
            existing = new GeneratedSlotChanges(key);
            _storageChanges.Add(key, existing);
        }
        return existing;
    }

    public void AddStorageRead(UInt256 key)
    {
        if (!_storageChanges.ContainsKey(key))
        {
            _storageReads.Add(key);
        }
    }

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.Append(Address);
        if (BalanceChanges.Count > 0) sb.Append($" balance=[{string.Join(", ", BalanceChanges)}]");
        if (NonceChanges.Count > 0) sb.Append($" nonce=[{string.Join(", ", NonceChanges)}]");
        if (CodeChanges.Count > 0) sb.Append($" code=[{string.Join(", ", CodeChanges)}]");
        if (_storageChanges.Count > 0)
        {
            using ArrayPoolListRef<GeneratedSlotChanges> sorted = GetSortedStorageChanges();
            AppendJoined(sb, " storage=[", sorted.AsSpan(), "]");
        }
        if (_storageReads.Count > 0)
        {
            using ArrayPoolListRef<UInt256> sorted = GetSortedStorageReads();
            AppendJoined(sb, " reads=[", sorted.AsSpan(), "]");
        }
        return sb.ToString();
    }

    private static void AppendJoined<T>(StringBuilder sb, string prefix, ReadOnlySpan<T> values, string suffix)
    {
        sb.Append(prefix);
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            if (values[i] is not null) sb.Append(values[i]!.ToString());
        }
        sb.Append(suffix);
    }
}
