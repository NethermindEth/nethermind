// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Microsoft.Extensions.Logging;
using Nethermind.BeaconChain.P2P;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>
/// Most mainnet dials fail, and the library logs each failed upgrade as an error whose text is the whole stack trace of the
/// failure, so the bridge must keep everything the library logs out of the levels an operator reads.
/// </summary>
public class Libp2pLoggingTests
{
    private const string UpgradeFailure = "Upgrade task failed with System.AggregateException: One or more errors occurred.\n ---> System.NullReferenceException\n   at Nethermind.Libp2p.Protocols.NoiseProtocol.DialAsync";

    [Test]
    public void Nothing_the_library_logs_reaches_a_level_above_trace([Values] LogLevel level)
    {
        TestLogRecorder logs = new();
        Microsoft.Extensions.Logging.ILogger libp2p = BeaconP2P.CreateLibp2pLoggerFactory(logs).CreateLogger("Nethermind.Libp2p.Core.LocalPeer");

        libp2p.Log(level, UpgradeFailure);

        Assert.That(logs.Lines.Select(static l => l.Level).Distinct(), Is.SubsetOf(new[] { "Trace" }));
    }
}
