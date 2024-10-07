// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Consensus.Requests;
using Nethermind.Core;
using Nethermind.Core.ConsensusRequests;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using NSubstitute;
using NUnit.Framework;


namespace Nethermind.Consensus.Test;

public class DepositProcessorTests
{
    [Test]
    public void ShouldProcessDeposit()
    {
        Block block = Build.A.Block.TestObject;
        DepositsProcessor depositsProcessor = new();

        var deposit = new Deposit()
        {
            Amount = 32000000000,
            Index = 0,
            Pubkey = Bytes.FromHexString(
                "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001"),
            Signature = Bytes.FromHexString(
                "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000003"),
            WithdrawalCredentials =
                Bytes.FromHexString("0000000000000000000000000000000000000000000000000000000000000002")
        };

        TxReceipt txReceipt = Build.A.Receipt.WithLogs(
            Build.A.LogEntry.WithData(
                    Bytes.FromHexString(
                        "00000000000000000000000000000000000000000000000000000000000000a000000000000000000000000000000000000000000000000000000000000001000000000000000000000000000000000000000000000000000000000000000140000000000000000000000000000000000000000000000000000000000000018000000000000000000000000000000000000000000000000000000000000002000000000000000000000000000000000000000000000000000000000000000030000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000080040597307000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000006000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000300000000000000000000000000000000000000000000000000000000000000080000000000000000000000000000000000000000000000000000000000000000"
                    )
                )
                .WithAddress(
                    new Core.Address(Bytes.FromHexString("0x00000000219ab540356cbb839cbe05303d7705fa"))
                ).TestObject
        ).TestObject;

        IReleaseSpec spec = Substitute.For<IReleaseSpec>();

        spec.DepositsEnabled.Returns(true);
        spec.DepositContractAddress.Returns(
            new Core.Address(Bytes.FromHexString("0x00000000219ab540356cbb839cbe05303d7705fa"))
        );

        var processedDeposits = depositsProcessor.ProcessDeposits(block, new[] { txReceipt }, spec).ToList();

        Assert.That(processedDeposits, Has.Count.EqualTo(1));

        Deposit processedDeposit = processedDeposits[0];

        processedDeposit.Amount.Should().Be(deposit.Amount);
        processedDeposit.Index.Should().Be(deposit.Index);
        processedDeposit.Pubkey?.Span.SequenceEqual(deposit.Pubkey.Value.Span).Should().BeTrue();
        processedDeposit.Signature?.SequenceEqual(deposit.Signature).Should().BeTrue();
        processedDeposit.WithdrawalCredentials?.SequenceEqual(deposit.WithdrawalCredentials).Should().BeTrue();
    }
}
