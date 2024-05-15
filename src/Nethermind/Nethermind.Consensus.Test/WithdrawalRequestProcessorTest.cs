// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using FluentAssertions;
using Nethermind.Consensus.Requests;
using Nethermind.Core.ConsensusRequests;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;


namespace Nethermind.Consensus.Test;

public class WithdrawalRequestProcessorTests
{
    [Test]
    [Explicit("This test is not yet implemented")]
    public void ShouldProcessWithdrawalRequest()
    {

        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.IsEip7002Enabled.Returns(true);
        spec.Eip7002ContractAddress.Returns(
            new Core.Address(Bytes.FromHexString("0x00a3ca265ebcb825b45f985a16cefb49958ce017"))
        );

        Core.Block block = Build.A.Block
                .WithHeader(
                    Build.A.BlockHeader
                        .WithParentHash(
                            new Hash256(Bytes.FromHexString("0x6ec1120707c89dc46895d187ebf345845d01ca6f79f6ed0a6b0d4232a63bff4f"))
                        )
                        .WithUnclesHash(
                            new Hash256(Bytes.FromHexString("0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347"))
                        )
                        .WithBeneficiary(
                            new Core.Address(Bytes.FromHexString("0x2adc25665018aa1fe0e6bc666dac8fc2697ff9ba"))
                        )
                        .WithDifficulty(UInt256.Zero)
                        .WithNumber(1)
                        .WithGasLimit(100000000000000000)
                        .WithTimestamp(12)
                        .WithHash(
                            new Hash256(Bytes.FromHexString("0x0f2f6d3487133112c7f801e3e6fdb24fdb774962836a83dba7e3085acccb91a3"))
                        )
                        .WithMixHash(
                            new Hash256("0x0000000000000000000000000000000000000000000000000000000000000000")
                        )
                        .WithReceiptsRoot(
                            new Hash256(Bytes.FromHexString("0x87048f1c285b93e2cc7ab5f4f2aec173c7bb1419236808c380c4d202f5d0527c"))
                        )
                        .WithRequestsRoot(
                            new Hash256(Bytes.FromHexString("0x42b195048f9da90a3c57186b9ac197ceca615cd47421287dc2c11ee8885598ff"))
                        )
                        .WithWithdrawalsRoot(
                            new Hash256(Bytes.FromHexString("0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421"))
                        )
                        .WithTransactionsRoot(
                            new Hash256(Bytes.FromHexString("0x5146d157c5e1d75d4493bcfc34f89f4f31a3add255683f201d4b2e0c21f003c0"))
                        )
                    .TestObject
                )
                .TestObject;

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();

        IWorldState state = Substitute.For<IWorldState>();
        state.AccountExists(spec.Eip7002ContractAddress).Returns(true);

        IVirtualMachine virtualMachine = Substitute.For<IVirtualMachine>();

        ILogManager logManager = Substitute.For<ILogManager>();

        WithdrawalRequestsProcessor withdrawalRequestsProcessor = new(
            new TransactionProcessor(specProvider, state, virtualMachine, logManager)
        );

        var withdrawalRequest = new WithdrawalRequest()
        {
            SourceAddress = new Core.Address(Bytes.FromHexString("0x0000000000000000000000000000000000000200")),
            ValidatorPubkey = Bytes.FromHexString("000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001"),
            Amount = 0
        };

        var withdrawalRequests = withdrawalRequestsProcessor.ReadWithdrawalRequests(spec, state, block).ToList();

        Assert.That(withdrawalRequests, Has.Count.EqualTo(1));

        WithdrawalRequest withdrawalRequestResult = withdrawalRequests[0];

        withdrawalRequestResult.Should().BeEquivalentTo(withdrawalRequest);
    }
}
