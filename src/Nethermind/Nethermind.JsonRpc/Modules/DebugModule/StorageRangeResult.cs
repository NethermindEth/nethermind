// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;
using Nethermind.State;

namespace Nethermind.JsonRpc.Modules.DebugModule;

/// <summary>A page of a contract's storage returned by <c>debug_storageRangeAt</c>, in geth's format.</summary>
public sealed class StorageRangeResult
{
    /// <summary>The maximum number of slots in one page, whatever the requested <c>maxResult</c>.</summary>
    public const int MaxResults = 1024;

    public StorageRangeResult(StorageRangeVisitor range)
    {
        if (range.HasStorage)
        {
            Storage = new Dictionary<Hash256, StorageRangeEntry>(range.Slots.Count);
            foreach ((ValueHash256 key, ValueHash256 value) in range.Slots)
            {
                Storage.Add(new Hash256(key), new StorageRangeEntry(new Hash256(value)));
            }
        }

        NextKey = range.NextKey is { } nextKey ? new Hash256(nextKey) : null;
    }

    /// <summary>The slots by hashed key, or <see langword="null"/> when the account has no storage, as in geth.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Dictionary<Hash256, StorageRangeEntry>? Storage { get; }

    /// <summary>The hashed key to continue from, or <see langword="null"/> when no slots follow.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Hash256? NextKey { get; }
}

/// <summary>A storage slot value; <see cref="Key"/> is always <see langword="null"/> because slot key preimages are not kept.</summary>
public sealed class StorageRangeEntry(Hash256 value)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Hash256? Key => null;

    public Hash256 Value { get; } = value;
}
