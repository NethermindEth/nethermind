// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.BeaconChain.Storage;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Db;

namespace Nethermind.BeaconChain.Test.Storage;

// Sorted tables support disk-like seeking; otherwise range reads must walk keys.
internal sealed class CountingStatesColumnsDb(bool sorted) : IColumnsDb<BeaconChainDbColumns>
{
    private readonly MemColumnsDb<BeaconChainDbColumns> _inner = new();

    public CountingStatesDb States { get; } = new();

    public CountingStatesDb Index { get; } = sorted ? new SeekableCountingStatesDb() : new CountingStatesDb();

    public IDb GetColumnDb(BeaconChainDbColumns key) => key == BeaconChainDbColumns.States ? States : key == BeaconChainDbColumns.StateSlotIndex ? Index : _inner.GetColumnDb(key);

    public IEnumerable<BeaconChainDbColumns> ColumnKeys => _inner.ColumnKeys;

    public IColumnsWriteBatch<BeaconChainDbColumns> StartWriteBatch() => new InMemoryColumnWriteBatch<BeaconChainDbColumns>(this);

    public IColumnDbSnapshot<BeaconChainDbColumns> CreateSnapshot() => throw new NotSupportedException();

    public void Dispose()
    {
        States.Dispose();
        Index.Dispose();
        _inner.Dispose();
    }

    public void Flush(bool onlyWal = false) { }

    internal class CountingStatesDb : MemDb, IDb
    {
        public int KeysRead { get; protected set; }

        public void ResetCount() => KeysRead = 0;

        public new IEnumerable<byte[]> GetAllKeys(bool ordered = false)
        {
            foreach (byte[] key in base.GetAllKeys(ordered))
            {
                KeysRead++;
                yield return key;
            }
        }
    }

    private sealed class SeekableCountingStatesDb : CountingStatesDb, ISortedKeyValueStore
    {
        public byte[]? FirstKey => Keys.MinBy(key => key, Bytes.Comparer);

        public byte[]? LastKey => Keys.MaxBy(key => key, Bytes.Comparer);

        public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive, ReadFlags flags = ReadFlags.None)
        {
            byte[] first = firstKeyInclusive.ToArray();
            byte[] last = lastKeyExclusive.ToArray();
            List<KeyValuePair<byte[], byte[]>> inRange = GetAll(ordered: true)
                .Where(pair => Bytes.BytesComparer.Compare(pair.Key, first) >= 0 && Bytes.BytesComparer.Compare(pair.Key, last) < 0)
                .ToList();
            return new CountingView(this, inRange);
        }

        private sealed class CountingView(SeekableCountingStatesDb owner, List<KeyValuePair<byte[], byte[]>> entries) : ISortedView
        {
            private int _index = -1;

            public bool StartBefore(ReadOnlySpan<byte> value) => throw new NotSupportedException();

            public bool MoveNext()
            {
                if (++_index >= entries.Count)
                {
                    return false;
                }

                owner.KeysRead++;
                return true;
            }

            public ReadOnlySpan<byte> CurrentKey => entries[_index].Key;

            public ReadOnlySpan<byte> CurrentValue => entries[_index].Value;

            public void Dispose() { }
        }
    }
}
