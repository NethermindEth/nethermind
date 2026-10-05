// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.BeaconChain.Storage;
using Nethermind.Core;
using Nethermind.Db;

namespace Nethermind.BeaconChain.Test.P2P;

internal sealed class SlotReadHookColumnsDb(ulong slot) : TestColumnsDb
{
    private readonly HookedIndex _index = new(slot);

    public Action? OnRead { set => _index.OnRead = value; }
    protected override IDb CreateColumn(BeaconChainDbColumns key) => key == BeaconChainDbColumns.BlockIndex ? _index : base.CreateColumn(key);

    private sealed class HookedIndex(ulong slot) : MemDb
    {
        public Action? OnRead;

        public override byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            if (key.Length == sizeof(ulong) && BinaryPrimitives.ReadUInt64BigEndian(key) == slot)
            {
                Interlocked.Exchange(ref OnRead, null)?.Invoke();
            }

            return base.Get(key, flags);
        }
    }
}
