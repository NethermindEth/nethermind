// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Serialization;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Per-account changes assembled from one or more <see cref="AccountChangesAtIndex"/> via merging.
/// Append-only and ordered by index; uses simple <see cref="List{T}"/> per change family because
/// merge contributions arrive sorted, so no <see cref="SortedList{TKey, TValue}"/> is needed.
/// </summary>
public class GeneratedAccountChanges(Address address)
{
    [JsonConverter(typeof(AddressConverter))]
    public Address Address { get; } = address;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<BalanceChange> BalanceChanges { get; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<NonceChange> NonceChanges { get; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<CodeChange> CodeChanges { get; } = [];

    private readonly SortedDictionary<UInt256, GeneratedSlotChanges> _storageChanges
        = new(GenericComparer.GetOptimized<UInt256>());
    private readonly SortedSet<UInt256> _storageReads
        = new(GenericComparer.GetOptimized<UInt256>());

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyCollection<GeneratedSlotChanges> StorageChanges => _storageChanges.Values;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyCollection<UInt256> StorageReads => _storageReads;

    public bool TryGetSlotChanges(UInt256 key, [NotNullWhen(true)] out GeneratedSlotChanges? slotChanges)
        => _storageChanges.TryGetValue(key, out slotChanges);

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

    /// <summary>Merge the per-index source into this accumulator. Caller must ensure indices arrive monotonically.</summary>
    public void Merge(AccountChangesAtIndex other)
    {
        if (other.BalanceChange is not null)
        {
            BalanceChanges.Add(other.BalanceChange.Value);
        }
        if (other.NonceChange is not null)
        {
            NonceChanges.Add(other.NonceChange.Value);
        }
        if (other.CodeChange is not null)
        {
            CodeChanges.Add(other.CodeChange.Value);
        }

        foreach (KeyValuePair<UInt256, StorageChange> kv in other.StorageChanges)
        {
            GeneratedSlotChanges slotChanges = GetOrAddSlotChanges(kv.Key);
            slotChanges.Changes.Add(kv.Value);
            // a change supersedes any prior read for the same slot
            _storageReads.Remove(kv.Key);
        }

        foreach (UInt256 read in other.StorageReads)
        {
            // only add reads where there's no existing change for the slot
            if (!_storageChanges.ContainsKey(read))
            {
                _storageReads.Add(read);
            }
        }
    }

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.Append(Address);
        if (BalanceChanges.Count > 0) sb.Append($" balance=[{string.Join(", ", BalanceChanges)}]");
        if (NonceChanges.Count > 0) sb.Append($" nonce=[{string.Join(", ", NonceChanges)}]");
        if (CodeChanges.Count > 0) sb.Append($" code=[{string.Join(", ", CodeChanges)}]");
        if (_storageChanges.Count > 0) sb.Append($" storage=[{string.Join(", ", _storageChanges.Values)}]");
        if (_storageReads.Count > 0) sb.Append($" reads=[{string.Join(", ", _storageReads)}]");
        return sb.ToString();
    }
}
