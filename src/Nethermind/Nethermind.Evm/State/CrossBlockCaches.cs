// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Evm.State;

/// <summary>
/// Cross-block caches for storage slots and account state. These survive across blocks
/// and are updated via write-through during block commit. Unlike <see cref="PreBlockCaches"/>,
/// these are never shared with the prewarmer — only the main processing thread reads/writes them.
/// </summary>
public class CrossBlockCaches
{
    private readonly SeqlockCache<StorageCell, byte[]> _storageCache = new();
    private readonly SeqlockCache<AddressAsKey, Account> _stateCache = new();

    public SeqlockCache<StorageCell, byte[]> StorageCache => _storageCache;
    public SeqlockCache<AddressAsKey, Account> StateCache => _stateCache;
}
