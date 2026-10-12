// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs.Forks;

namespace Nethermind.Stateless.Execution.IO;

public static partial class ProtocolForkExtensions
{
    private static readonly Dictionary<string, ProtocolFork> _forksByName = new(StringComparer.Ordinal)
    {
        [Cancun.Instance.Name] = ProtocolFork.Cancun,
        [Prague.Instance.Name] = ProtocolFork.Prague,
        [Osaka.Instance.Name] = ProtocolFork.Osaka,
        [BPO1.Instance.Name] = ProtocolFork.BPO1,
        [BPO2.Instance.Name] = ProtocolFork.BPO2,
        [Amsterdam.Instance.Name] = ProtocolFork.Amsterdam
    };
}
