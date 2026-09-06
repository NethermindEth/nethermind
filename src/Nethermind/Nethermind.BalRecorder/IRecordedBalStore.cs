// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;

namespace Nethermind.BalRecorder;

public interface IRecordedBalStore : IDisposable
{
    void Insert(Block block, GeneratedBlockAccessList bal);
    ReadOnlyBlockAccessList? Get(ulong blockNumber);
    bool ReplayEnabled { get; }
    bool RecordingEnabled { get; }
}
