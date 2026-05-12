// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State;

/// <summary>
/// Persists the absolute oldest-state-block floor in the metadata DB. Owned by the world-state
/// manager since the value is a state-availability concern (set by snap-sync finalization and
/// full-pruning runs).
/// </summary>
public sealed class OldestStateBlockStore(IDb metadataDb)
{
    private long? _value = metadataDb.Get(MetadataDbKeys.OldestStateBlock)?.AsRlpValueContext().DecodeLong();

    public long? Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            if (value.HasValue)
            {
                metadataDb.Set(MetadataDbKeys.OldestStateBlock, Rlp.Encode(value.Value).Bytes);
            }
        }
    }
}
