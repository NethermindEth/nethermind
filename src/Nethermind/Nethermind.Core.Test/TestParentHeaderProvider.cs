// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.State;

namespace Nethermind.Core.Test;

public sealed class TestParentHeaderProvider : IParentHeaderProvider
{
    public static TestParentHeaderProvider Instance { get; } = new();

    private TestParentHeaderProvider() { }

    public BlockHeader? FindParentHeader(BlockHeader target) => null;
}
