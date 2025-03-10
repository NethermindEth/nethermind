// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Optimism.ProtocolVersion;

namespace Nethermind.Optimism;

public static class OptimismConstants
{
    public const long PreRegolithNonZeroCountOverhead = 68;

    /// <remarks>
    /// See <see href="https://specs.optimism.io/protocol/superchain-upgrades.html#op-stack-protocol-versions"/>
    /// </remarks>
    public static OptimismProtocolVersion CurrentProtocolVersion { get; } = new OptimismProtocolVersion.V0(
        build: [(byte)'N', (byte)'E', (byte)'T', (byte)'H', 0, 0, 0, 0],
        major: 9,
        minor: 0,
        patch: 0,
        preRelease: 0);
}
