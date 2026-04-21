// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;

namespace Nethermind.BalRecorder;

public interface IRecordedBalStore : IDisposable
{
    void Insert(Block block, BlockAccessList bal);
    /// <summary>Retrieves the recorded BAL for <paramref name="blockNumber"/>. <paramref name="blockHash"/> is accepted for caller convenience but is not used as part of the key — the store is keyed by block number only and is not reorg-safe.</summary>
    BlockAccessList? Get(long blockNumber, Hash256 blockHash);
    bool ReplayEnabled { get; }
    bool RecordingEnabled { get; }
}
