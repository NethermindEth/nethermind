// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Taiko.Precompiles;

/// <summary>
/// Retrieves storage values from L1 contracts via eth_getStorageAt RPC.
/// </summary>
public interface IL1StorageProvider
{
    UInt256? GetStorageValue(Address contractAddress, UInt256 blockNumber, UInt256 storageKey);
}
