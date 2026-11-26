// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Config;

namespace Nethermind.Network.Benchmarks;

/// <summary>
/// The round trips of <see cref="RlpxRoundTripBenchmarks"/> with both nodes on the socket transport.
/// </summary>
/// <remarks>A separate class keeps the DotNetty rows' names, so they still compare with master's.</remarks>
public class SocketRlpxRoundTripBenchmarks : RlpxRoundTripBenchmarks
{
    protected override RlpxTransport Transport => RlpxTransport.Sockets;
}
