// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Merge.AuRa.Contracts;
using Nethermind.Merge.AuRa.Withdrawals;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.AuRa.Test;

public class AuraWithdrawalProcessorTests
{
    [Test]
    public void Should_invoke_contract_as_expected()
    {
        var contract = Substitute.For<IWithdrawalContract>();
        var logManager = Substitute.For<ILogManager>();
        var withdrawalProcessor = new AuraWithdrawalProcessor(contract, logManager);
        var block = Build.A.Block
            .WithNumber(123)
            .WithWithdrawals(
                new[]
                {
                    Build.A.Withdrawal
                        .WithAmount(1_000_000UL)
                        .WithRecipient(Address.SystemUser).TestObject,
                    Build.A.Withdrawal
                        .WithAmount(2_000_000UL)
                        .WithRecipient(Address.Zero).TestObject
                })
            .TestObject;
        var spec = Substitute.For<IReleaseSpec>();

        spec.WithdrawalsEnabled.Returns(true);

        // we need to capture those values, because the ArrayPools will be disposed before we can match them
        ulong[] values = Array.Empty<ulong>();
        Address[] addresses = Array.Empty<Address>();
        contract.ExecuteWithdrawals(
            block.Header,
            Arg.Do<IList<ulong>>(a => values = a.ToArray()),
            Arg.Do<IList<Address>>(a => addresses = a.ToArray()));

        withdrawalProcessor.ProcessWithdrawals(block, spec);

        contract
            .Received(1)
            .ExecuteWithdrawals(
                Arg.Is(block.Header),
                Arg.Is<IList<ulong>>(a => values.SequenceEqual(new[] { 1_000_000UL, 2_000_000UL })),
                Arg.Is<IList<Address>>(a => addresses.SequenceEqual(new[] { Address.SystemUser, Address.Zero })));
    }

    [Test]
    public void Should_not_invoke_contract_before_Shanghai()
    {
        var contract = Substitute.For<IWithdrawalContract>();
        var logManager = Substitute.For<ILogManager>();
        var withdrawalProcessor = new AuraWithdrawalProcessor(contract, logManager);
        var block = Build.A.Block.TestObject;
        var spec = Substitute.For<IReleaseSpec>();

        spec.WithdrawalsEnabled.Returns(false);

        withdrawalProcessor.ProcessWithdrawals(block, spec);

        contract
            .Received(0)
            .ExecuteWithdrawals(
                Arg.Any<BlockHeader>(),
                Arg.Any<ulong[]>(),
                Arg.Any<Address[]>());
    }
}
