// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.Precompiles;

/// <summary>
/// Interface for L1 storage providers that can retrieve storage values from L1 contracts.
/// </summary>
public interface IL1StorageProvider
{
    UInt256? GetStorageValue(Address contractAddress, UInt256 storageKey, UInt256 blockNumber);
}
