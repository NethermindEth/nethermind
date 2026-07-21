// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;

namespace Nethermind.BalRecorder.Test;

/// <summary>In-memory <see cref="IRecordedBalStore"/> for decorator tests.</summary>
internal sealed class FakeRecordedBalStore : IRecordedBalStore
{
    private readonly Dictionary<ulong, ReadOnlyBlockAccessList> _seeded = [];

    public bool ReplayEnabled { get; init; }
    public bool RecordingEnabled { get; init; }
    public List<(ulong Number, GeneratedBlockAccessList Bal)> Inserted { get; } = [];

    public void Seed(ulong number, ReadOnlyBlockAccessList bal) => _seeded[number] = bal;
    public void Insert(Block block, GeneratedBlockAccessList bal) => Inserted.Add((block.Number, bal));
    public ReadOnlyBlockAccessList? Get(ulong blockNumber) => _seeded.GetValueOrDefault(blockNumber);
    public void Dispose() { }
}
