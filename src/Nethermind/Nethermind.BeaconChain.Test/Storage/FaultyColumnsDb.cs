// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.BeaconChain.Storage;
using Nethermind.Core;
using Nethermind.Db;

namespace Nethermind.BeaconChain.Test.Storage;

internal sealed class FaultyColumnsDb : IColumnsDb<BeaconChainDbColumns>
{
    private readonly MemColumnsDb<BeaconChainDbColumns> _inner = new();
    private readonly FaultyMemDb _sidecars = new();
    private readonly SlotReadHookMemDb _canonicalIndex = new();

    public bool FailReads { set => _sidecars.FailReads = value; }

    public bool FailWrites { set => _sidecars.FailWrites = value; }

    public bool FailDeletes { set => _sidecars.FailDeletes = value; }

    // Callback runs after the next 40-byte record read, before its consumer continues.
    public Action? AfterNextRecordRead { set => _sidecars.AfterNextRecordRead = value; }

    // Callback blocks canonical-slot readers before their underlying read.
    public Action? BeforeCanonicalSlotRead { set => _canonicalIndex.BeforeSlotRead = value; }

    public IDb GetColumnDb(BeaconChainDbColumns key) => key switch
    {
        BeaconChainDbColumns.DataColumnSidecars => _sidecars,
        BeaconChainDbColumns.BlockIndex => _canonicalIndex,
        _ => _inner.GetColumnDb(key),
    };

    public IEnumerable<BeaconChainDbColumns> ColumnKeys => _inner.ColumnKeys;

    public IColumnsWriteBatch<BeaconChainDbColumns> StartWriteBatch() => new InMemoryColumnWriteBatch<BeaconChainDbColumns>(this);

    public IColumnDbSnapshot<BeaconChainDbColumns> CreateSnapshot() => throw new NotSupportedException();

    public void Dispose() { }

    public void Flush(bool onlyWal = false) { }

    private sealed class SlotReadHookMemDb : MemDb
    {
        public Action? BeforeSlotRead { get; set; }

        public override byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            if (key.Length == sizeof(ulong))
            {
                BeforeSlotRead?.Invoke();
            }

            return base.Get(key, flags);
        }
    }

    private sealed class FaultyMemDb : MemDb
    {
        public bool FailReads { get; set; }

        public bool FailWrites { get; set; }

        public bool FailDeletes { get; set; }

        public Action? AfterNextRecordRead { get; set; }

        public override byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            byte[]? value = FailReads ? throw new ObjectDisposedException("the data column table") : base.Get(key, flags);
            if (key.Length == 40 && AfterNextRecordRead is { } after)
            {
                AfterNextRecordRead = null;
                after();
            }

            return value;
        }

        public override void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            if (FailWrites || (FailDeletes && value is null))
            {
                throw new InvalidOperationException("the data column table refuses writes");
            }

            base.Set(key, value, flags);
        }
    }
}
