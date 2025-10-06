// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core;

namespace Nethermind.State.Healing;

[method: DebuggerStepThrough]
public class HealingCodeDb(IKeyValueStoreWithBatching codeDb, Lazy<ICodeRecovery> recovery) : IKeyValueStoreWithBatching
{
    public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
    {
        byte[]? bytes = codeDb.Get(key, flags);
        if (bytes is null)
        {
            bytes = recovery.Value.Recover(key.ToArray()).GetAwaiter().GetResult();
            if (bytes is not null)
            {
                Set(key, bytes);
            }
        }

        return bytes;
    }

    public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
    {
        codeDb.Set(key, value, flags);
    }

    public IWriteBatch StartWriteBatch() => codeDb.StartWriteBatch();
}
