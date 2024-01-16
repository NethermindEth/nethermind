// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Facade.Filters;
using Nethermind.Int256;
using Nethermind.Merge.AuRa.Shutter;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.AuRa.Test;

class ShutterTxSourceTests
{
    private ShutterTxSource? _shutterTxSource;

    [SetUp]
    public void SetUp()
    {
        ILogFinder logFinder = Substitute.For<ILogFinder>();

        List<IFilterLog> logs = new();

        for (byte i = 0; i < 5; i++)
        {
            IFilterLog log = Substitute.For<IFilterLog>();

            byte[] encryptedData = Enumerable.Repeat(i, 5).ToArray();
            byte[] identity = Enumerable.Repeat((byte)0, 32).ToArray();
            object[] data = [0L, identity, Address.Zero, encryptedData, new UInt256(100)];
            byte[] encodedData = AbiEncoder.Instance.Encode(AbiEncodingStyle.None, ShutterTxSource.TransactionSubmmitedSig, data);

            log.Data.Returns(encodedData); ;
            logs.Add(log);
        }

        logFinder.FindLogs(Arg.Any<LogFilter>()).Returns(logs);
        _shutterTxSource = new(logFinder, new FilterStore());
    }

    [Test]
    public void Can_get_transactions_from_logs()
    {
        IEnumerable<ShutterTxSource.SequencedTransaction> txs = _shutterTxSource!.GetNextTransactions(0, 0);
        txs.Count().Should().Be(3);
        txs.ElementAt(0).EncryptedTransaction.Should().Equal([0, 0, 0, 0, 0]);
        txs.ElementAt(1).EncryptedTransaction.Should().Equal([1, 1, 1, 1, 1]);
        txs.ElementAt(2).EncryptedTransaction.Should().Equal([2, 2, 2, 2, 2]);
    }
}
