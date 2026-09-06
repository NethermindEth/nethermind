// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Healing;

/// <summary>
/// Code database that fetches bytecode missing locally from the network and persists it.
/// </summary>
[method: DebuggerStepThrough]
public class HealingCodeDb(IKeyValueStoreWithBatching codeDb, Lazy<ICodeRecovery> recovery) : IKeyValueStoreWithBatching
{
    /// <summary>
    /// Reads the bytecode stored under <paramref name="key"/>, recovering it from peers when it is missing locally.
    /// </summary>
    /// <remarks>
    /// On a miss, blocks while the bytecode is requested from peers, then writes it back so the next
    /// read is served locally. Only 32-byte keys are recoverable — anything else is a plain miss.
    /// </remarks>
    public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
    {
        byte[]? bytes = codeDb.Get(key, flags);
        if (bytes is null && key.Length == ValueHash256.MemorySize)
        {
            bytes = recovery.Value.Recover(new ValueHash256(key)).GetAwaiter().GetResult();
            if (bytes is not null)
            {
                Set(key, bytes);
            }
        }

        return bytes;
    }

    public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) =>
        codeDb.Set(key, value, flags);

    public IWriteBatch StartWriteBatch() => codeDb.StartWriteBatch();
}
