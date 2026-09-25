// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

internal static class PbtPartitions
{
    /// <summary>The <see cref="PbtPartition"/> a complete key folds in, or -1 when it lies in no partition.</summary>
    internal static int PartitionOf<TKey>(TKey key) where TKey : struct, IPbtKey<TKey> => key.Length == 0 ? -1 : key.Bytes[0] switch
    {
        Eip8297KeyDerivation.AccountZone when key.Length >= Eip8297KeyDerivation.AccountKeyLength => (int)PbtPartition.Account,
        Eip8297KeyDerivation.CodeZone when key.Length >= Eip8297KeyDerivation.AccountKeyLength => (int)PbtPartition.Code,
        Eip8297KeyDerivation.StorageZone when key.Length >= Eip8297KeyDerivation.StorageKeyLength => (int)PbtPartition.Storage,
        _ => -1,
    };
}
