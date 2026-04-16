// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;

namespace Nethermind.BalRecorder;

public interface IRecordedBalStore
{
    void Insert(Block block, BlockAccessList bal);
    BlockAccessList? Get(long blockNumber, Hash256 blockHash);
    bool ReplayEnabled { get; }
    bool RecordingEnabled { get; }
}
