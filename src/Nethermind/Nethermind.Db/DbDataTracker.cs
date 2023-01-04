// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Db
{
    public class DbDataTracker : IDb
    {
        private readonly IDb _innerDb;
        private readonly Action<int> _dataPlaced;

        public DbDataTracker(IDb rocksDb, Action<int> dataPlaced)
        {
            _innerDb = rocksDb;
            _dataPlaced = dataPlaced;
        }

        public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys] => _innerDb[keys];

        public byte[]? this[byte[] key]
        {
            get => _innerDb[key];
            set
            {
                if (value is not null)
                    _dataPlaced.Invoke(value.Length);
                _innerDb[key] = value;
            }
        }

        byte[]? IReadOnlyKeyValueStore.this[byte[] key] => _innerDb[key];

        public string Name => _innerDb.Name;

        public void Clear() => _innerDb.Clear();

        public void Dispose() => _innerDb.Dispose();

        public void Flush() => _innerDb.Flush();

        public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false) => _innerDb.GetAll(ordered);

        public IEnumerable<byte[]> GetAllValues(bool ordered = false) => _innerDb.GetAllValues(ordered);

        public bool KeyExists(byte[] key) => _innerDb.KeyExists(key);

        public void Remove(byte[] key) => _innerDb.Remove(key);

        public IBatch StartBatch() => new DbDataTrackerBatch(_innerDb.StartBatch(), _dataPlaced);
    }

    public class DbDataTrackerBatch : IBatch
    {
        private readonly IBatch _batch;
        private readonly Action<int> _dataPlaced;

        public DbDataTrackerBatch(IBatch batch, Action<int> dataPlaced)
        {
            _batch = batch;
            _dataPlaced = dataPlaced;
        }

        public byte[]? this[byte[] key]
        {
            get => _batch[key];
            set
            {
                if (value is not null)
                    _dataPlaced.Invoke(value.Length);
                _batch[key] = value;
            }
        }

        byte[]? IReadOnlyKeyValueStore.this[byte[] key] => _batch[key];

        public void Dispose() => _batch.Dispose();
    }
}
