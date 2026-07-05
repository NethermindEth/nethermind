// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State;

/// <summary>
/// Trie backend's <see cref="IStateBoundary"/>. The oldest-state-block floor is co-located with
/// the trie nodes in the state DB (so wiping the state directory drops it); the best-persisted-state
/// ceiling stays in the BlockInfos DB where the block tree has always kept it. Flat does not use
/// this store — its boundary reads through the persistence manager directly.
/// </summary>
public sealed class StateBoundaryStore(
    IKeyValueStore stateDb,
    IKeyValueStore blockInfosDb,
    ulong? retentionWindowBlocks,
    ILogManager? logManager = null) : IStateBoundary, IStateBoundaryWriter
{
    /// <summary>
    /// 32-byte keccak slot key, collision-free against the trie DB's hash-keyed (32 bytes of
    /// state-root-hash entropy) and HalfPath-keyed nodes.
    /// </summary>
    internal static readonly byte[] OldestStateBlockKey = Keccak.Compute("OldestStateBlock").BytesToArray();

    /// <summary>The BlockInfos DB key the block tree has always used for the best-persisted-state block.</summary>
    internal static readonly byte[] BestPersistedStateKey = new byte[16];

    private readonly ILogger _logger = logManager?.GetClassLogger<StateBoundaryStore>() ?? default;
    private readonly Lock _lock = new();
    private ulong? _oldestStateBlock = DecodeBlockNumber(stateDb[OldestStateBlockKey]);
    private ulong? _bestPersistedState = DecodeBlockNumber(blockInfosDb[BestPersistedStateKey]);

    public ulong? RetentionWindowBlocks => retentionWindowBlocks;

    public ulong? OldestStateBlock
    {
        get
        {
            lock (_lock) return _oldestStateBlock;
        }
        set
        {
            lock (_lock)
            {
                if (_oldestStateBlock == value) return;
                // Reject backward non-null writes; null reset is permitted for recovery.
                if (value.HasValue && _oldestStateBlock.HasValue && value.Value < _oldestStateBlock.Value)
                {
                    if (_logger.IsWarn)
                        _logger.Warn($"Rejected backward OldestStateBlock write {value.Value} (current floor {_oldestStateBlock.Value}); kept current.");
                    return;
                }
                // Persist before caching so a thrown kv write doesn't desync memory from disk.
                if (value.HasValue)
                    stateDb[OldestStateBlockKey] = Rlp.Encode(value.Value).Bytes;
                else
                    stateDb.Remove(OldestStateBlockKey);
                _oldestStateBlock = value;
            }
        }
    }

    /// <summary>
    /// Highest block whose state is durably persisted. Last-write-wins — unlike the
    /// <see cref="OldestStateBlock"/> floor, boundaries legitimately move backward after deep
    /// rewinds, so no monotonic guard.
    /// </summary>
    public ulong? BestPersistedState
    {
        get
        {
            lock (_lock) return _bestPersistedState;
        }
        set
        {
            lock (_lock)
            {
                if (_bestPersistedState == value) return;
                // Persist before caching so a thrown kv write doesn't desync memory from disk.
                if (value.HasValue)
                    blockInfosDb[BestPersistedStateKey] = Rlp.Encode(value.Value).Bytes;
                else
                    blockInfosDb.Remove(BestPersistedStateKey);
                _bestPersistedState = value;
            }
        }
    }

    public bool TryGetBestPersistedState(out ulong blockNumber, [NotNullWhen(true)] out Hash256? stateRoot)
    {
        blockNumber = 0;
        stateRoot = null;
        return false;
    }

    private static ulong? DecodeBlockNumber(byte[]? rlp) =>
        rlp is null ? null : new RlpReader(rlp).DecodeULong();
}
