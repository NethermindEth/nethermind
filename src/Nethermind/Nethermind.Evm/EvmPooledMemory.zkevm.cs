// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;

namespace Nethermind.Evm;

public partial struct EvmPooledMemory
{
    // zkVM is single-threaded; SafeArrayPool's pow2 bucket pool already provides retention,
    // so there is no shared-pool contention to avoid and a thread-local cache would only
    // bypass the bucket pool's reuse. Buffers go straight to/from the pool.
    private static void StashBuffer(byte[] memory) => SafeArrayPool<byte>.Shared.Return(memory);

    private static byte[]? TryReuseBuffer(int wanted) => null;
}
