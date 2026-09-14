// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.State;

namespace Nethermind.Core.Test;

public sealed class TestParentHeaderProvider : IParentHeaderProvider
{
    public static TestParentHeaderProvider Instance { get; } = new();

    public BlockHeader? Parent { get; set; }
    public BlockHeader? LastTarget { get; private set; }

    public TestParentHeaderProvider() { }

    public BlockHeader? FindParentHeader(BlockHeader target)
    {
        LastTarget = target;
        return Parent;
    }

    public ulong FinalizedBlockNumber => 0;

    public BlockHeader? GetFinalizedHeader(ulong blockNumber) => null;
}
