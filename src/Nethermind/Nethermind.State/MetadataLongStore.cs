// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State;

/// <summary>
/// Persists a single nullable long value in the metadata DB under a fixed key. Reads and writes
/// are locked because <see cref="System.Nullable{T}"/> of <see cref="long"/> isn't torn-read safe
/// when the RPC path reads concurrently with sync/pruner writes.
/// </summary>
public class MetadataLongStore(IDb metadataDb, int key)
{
    private readonly Lock _lock = new();
    private long? _value = metadataDb.Get(key)?.AsRlpValueContext().DecodeLong();

    public long? Value
    {
        get
        {
            lock (_lock) return _value;
        }
        set
        {
            lock (_lock)
            {
                if (_value == value) return;
                _value = value;
                if (value.HasValue)
                    metadataDb.Set(key, Rlp.Encode(value.Value).Bytes);
                else
                    metadataDb.Delete(key);
            }
        }
    }
}
