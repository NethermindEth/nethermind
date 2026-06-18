// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State;

/// <summary>
/// Persists the oldest-state-block floor for the trie backend, co-located with the trie nodes
/// in the state DB so wiping the state directory drops the floor automatically. Flat does not
/// use this store — its <see cref="IStateBoundary.OldestStateBlock"/> reads through the
/// persistence manager directly.
/// </summary>
public sealed class StateBoundaryStore(IKeyValueStore kv, ILogManager? logManager = null)
{
    /// <summary>
    /// 32-byte keccak slot key, collision-free against the trie DB's hash-keyed (32 bytes of
    /// state-root-hash entropy) and HalfPath-keyed nodes.
    /// </summary>
    internal static readonly byte[] OldestStateBlockKey = Keccak.Compute("OldestStateBlock").BytesToArray();

    private readonly ILogger _logger = logManager?.GetClassLogger<StateBoundaryStore>() ?? default;
    private readonly Lock _lock = new();
    private ulong? _value = kv[OldestStateBlockKey]?.AsRlpValueContext().DecodeULong();

    public ulong? OldestStateBlock
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
                // Reject backward non-null writes; null reset is permitted for recovery.
                if (value.HasValue && _value.HasValue && value.Value < _value.Value)
                {
                    if (_logger.IsWarn)
                        _logger.Warn($"Rejected backward OldestStateBlock write {value.Value} (current floor {_value.Value}); kept current.");
                    return;
                }
                // Persist before caching so a thrown kv write doesn't desync memory from disk.
                if (value.HasValue)
                    kv[OldestStateBlockKey] = Rlp.Encode(value.Value).Bytes;
                else
                    kv.Remove(OldestStateBlockKey);
                _value = value;
            }
        }
    }
}
