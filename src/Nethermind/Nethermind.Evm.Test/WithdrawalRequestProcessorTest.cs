// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using FluentAssertions;
using Nethermind.Consensus.Requests;
using Nethermind.Core;
using Nethermind.Core.ConsensusRequests;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;


namespace Nethermind.Evm.Test;

public class WithdrawalRequestProcessorTests
{

    private ISpecProvider _specProvider;
    private IEthereumEcdsa _ethereumEcdsa;
    private TransactionProcessor _transactionProcessor;
    private IWorldState _stateProvider;

    private static readonly UInt256 AccountBalance = 1.Ether();

    private readonly Address eip7002Account = new(Bytes.FromHexString("0x00a3ca265ebcb825b45f985a16cefb49958ce017"));

    [SetUp]
    public void Setup()
    {
        _specProvider = MainnetSpecProvider.Instance;
        MemDb stateDb = new();
        TrieStore trieStore = new(stateDb, LimboLogs.Instance);
        _stateProvider = new WorldState(trieStore, new MemDb(), LimboLogs.Instance);
        _stateProvider.CreateAccount(eip7002Account, AccountBalance);
        _stateProvider.Commit(_specProvider.GenesisSpec);
        _stateProvider.CommitTree(0);
        _stateProvider.StateRoot =new Hash256(Bytes.FromHexString("0x2b4d2dada0e987cf9ad952f7a0f962c371073a2900d42da2778a4f8087d66811"));

        VirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        _transactionProcessor = new TransactionProcessor(_specProvider, _stateProvider, virtualMachine, LimboLogs.Instance);
        _ethereumEcdsa = new EthereumEcdsa(_specProvider.ChainId, LimboLogs.Instance);
    }


    [Test]
    [Explicit("This test is not yet implemented")]
    public void ShouldProcessWithdrawalRequest()
    {

        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.IsEip7002Enabled.Returns(true);
        spec.Eip7002ContractAddress.Returns(eip7002Account);

        Core.Block block = Build.A.Block
                .WithHeader(
                    Build.A.BlockHeader
                        .WithParentHash(
                            new Hash256(Bytes.FromHexString("0x825d0a25f8989de79a07be308f626a275930df1d1a41df7211ed67ce0cacb1c7"))
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
                            new Hash256(Bytes.FromHexString("0x4f01465e6b7a381227d736639f72e645b7023a223bb4e1df7fcd11555bb789be"))
                        )
                        .WithMixHash(
                            new Hash256("0x0000000000000000000000000000000000000000000000000000000000000000")
                        )
                        .WithReceiptsRoot(
                            new Hash256(Bytes.FromHexString("0x12a36f1d9611048f267288cd54c8c3f96cdfcdbaf3547eb91cfa71012c6b4120"))
                        )
                        .WithRequestsRoot(
                            new Hash256(Bytes.FromHexString("0xe922b6c8c2a1aef3e0886455d4b791e8351d6213d38814aa5a1170e5e1af59a1"))
                        )
                        .WithWithdrawalsRoot(
                            new Hash256(Bytes.FromHexString("0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421"))
                        )
                        .WithTransactionsRoot(
                            new Hash256(Bytes.FromHexString("0x6415cfd997ac8058810eb6866dfcf25f23da303840f34adddf68bef6c95dbf5d"))
                        )
                    .TestObject
                )
                .TestObject;

        WithdrawalRequestsProcessor withdrawalRequestsProcessor = new(transactionProcessor: _transactionProcessor);

        var withdrawalRequest = new WithdrawalRequest()
        {
            SourceAddress = new Address(Bytes.FromHexString("0x0000000000000000000000000000000000000200")),
            ValidatorPubkey = Bytes.FromHexString("000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001"),
            Amount = 0
        };

        var withdrawalRequests = withdrawalRequestsProcessor.ReadWithdrawalRequests(spec, _stateProvider, block).ToList();

        Assert.That(withdrawalRequests, Has.Count.EqualTo(1));

        WithdrawalRequest withdrawalRequestResult = withdrawalRequests[0];

        withdrawalRequestResult.Should().BeEquivalentTo(withdrawalRequest);
    }
}
