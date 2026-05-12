// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State;

/// <summary>
/// Persists the absolute oldest-state-block floor in the metadata DB. Locked because
/// <see cref="System.Nullable{T}"/> of <see cref="long"/> isn't torn-read safe and the RPC
/// path reads concurrently with sync/pruner writes.
/// </summary>
public sealed class OldestStateBlockStore(IDb metadataDb)
{
    private readonly Lock _lock = new();
    private long? _value = metadataDb.Get(MetadataDbKeys.OldestStateBlock)?.AsRlpValueContext().DecodeLong();

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
                    metadataDb.Set(MetadataDbKeys.OldestStateBlock, Rlp.Encode(value.Value).Bytes);
                else
                    metadataDb.Delete(MetadataDbKeys.OldestStateBlock);
            }
        }
    }
}
