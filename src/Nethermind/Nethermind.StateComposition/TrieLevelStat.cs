// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.StateComposition;

public readonly record struct TrieLevelStat
{
    public int Depth { get; init; }
    public long BranchNodes { get; init; }
    public long ExtensionNodes { get; init; }
    public long LeafNodes { get; init; }
    public long ByteSize { get; init; }
}
