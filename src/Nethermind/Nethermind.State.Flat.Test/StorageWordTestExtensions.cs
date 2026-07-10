// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Test conveniences bridging the minimal-length <c>byte[]</c> the tests express storage values in and the
/// fixed-width <c>EvmWord</c> the storage seam now carries.
/// </summary>
internal static class StorageWordTestExtensions
{
    public static EvmWord ToWord(this byte[] bytes) => StorageWord.FromStorageBytes(bytes);

    public static byte[] ToStorageBytes(this EvmWord word) => StorageWord.ToStorageBytes(in word, out _).ToArray();

    public static void Set(this IWorldStateScopeProvider.IStorageWriteBatch batch, in UInt256 index, byte[] value)
        => batch.Set(in index, StorageWord.FromStorageBytes(value));
}
