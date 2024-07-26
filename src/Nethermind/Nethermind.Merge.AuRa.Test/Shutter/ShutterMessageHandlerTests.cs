// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Merge.AuRa.Shutter;
using NUnit.Framework;
using NSubstitute;
using Nethermind.Logging;
using Nethermind.Consensus.AuRa.Config;

namespace Nethermind.Merge.AuRa.Test;


class ShutterMessageHandlerTests
{
    [Test]
    public void Can_load_decryption_keys()
    {
        ShutterConfig cfg = new()
        {
        };

        ShutterTxSource txSource = Substitute.For<ShutterTxSource>();

        txSource
            .When(x => x.LoadTransactions(Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<List<(byte[], byte[])>>()))
            .Do(x => { return; });

        ShutterEon eon = Substitute.For<ShutterEon>();
        eon.GetCurrentEonInfo().Returns(x => null);

        ShutterMessageHandler handler = new ShutterMessageHandler(cfg, txSource, eon, LimboLogs.Instance);
    }
}
