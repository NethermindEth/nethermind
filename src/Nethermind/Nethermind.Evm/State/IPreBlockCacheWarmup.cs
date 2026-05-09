// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Eip2930;

namespace Nethermind.Evm.State;

/// <summary>
/// Creates direct pre-block cache warmup sessions for account and storage reads.
/// </summary>
public interface IPreBlockCacheWarmup
{
    /// <summary>
    /// Begins a direct account and storage cache warmup session rooted at <paramref name="baseBlock" />.
    /// </summary>
    IPreBlockCacheWarmupSession BeginPreBlockCacheWarmup(BlockHeader? baseBlock);
}

/// <summary>
/// Direct account and storage reader used only for pre-block cache warmup.
/// </summary>
public interface IPreBlockCacheWarmupSession : IDisposable
{
    /// <summary>
    /// Whether this session can be used concurrently by multiple warmup workers.
    /// </summary>
    bool CanBeShared { get; }

    /// <summary>
    /// Warms the account cache entry for <paramref name="address" />.
    /// </summary>
    bool WarmUp(Address address);

    /// <summary>
    /// Warms and returns the storage cache entry for <paramref name="storageCell" />.
    /// </summary>
    ReadOnlySpan<byte> Get(in StorageCell storageCell);
}

public static class PreBlockCacheWarmupSessionExtensions
{
    /// <summary>
    /// Walks <paramref name="accessList"/>, warming each address and its storage slots through
    /// <paramref name="session"/>. Storage slot warmup is skipped when the account doesn't exist
    /// (saves the per-slot trie walk for missing accounts).
    /// </summary>
    public static void WarmUp(this IPreBlockCacheWarmupSession session, AccessList accessList)
    {
        foreach ((Address address, AccessList.StorageKeysEnumerable storages) in accessList)
        {
            if (!session.WarmUp(address)) continue;

            foreach (Int256.UInt256 storage in storages)
            {
                session.Get(new StorageCell(address, in storage));
            }
        }
    }
}
