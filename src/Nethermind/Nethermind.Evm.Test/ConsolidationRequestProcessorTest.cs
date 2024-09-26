// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using FluentAssertions;
using Nethermind.Consensus.Requests;
using Nethermind.Core;
using Nethermind.Core.ConsensusRequests;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;


namespace Nethermind.Evm.Test;

public class ConsolidationRequestProcessorTests
{

    private ISpecProvider _specProvider;
    private ITransactionProcessor _transactionProcessor;
    private IWorldState _stateProvider;

    private ICodeInfoRepository _codeInfoRepository;

    private static readonly UInt256 AccountBalance = 1.Ether();

    private readonly Address eip7251Account = Eip7251Constants.ConsolidationRequestPredeployAddress;

    [SetUp]
    public void Setup()
    {
        _specProvider = MainnetSpecProvider.Instance;
        MemDb stateDb = new();
        TrieStore trieStore = new(stateDb, LimboLogs.Instance);
        _stateProvider = new WorldState(trieStore, new MemDb(), LimboLogs.Instance);
        _stateProvider.CreateAccount(eip7251Account, AccountBalance);
        _stateProvider.Commit(_specProvider.GenesisSpec);
        _stateProvider.CommitTree(0);

        _codeInfoRepository = new CodeInfoRepository(_specProvider.ChainId);

        VirtualMachine virtualMachine = new(new TestBlockhashProvider(_specProvider), _specProvider, _codeInfoRepository, LimboLogs.Instance);

        _transactionProcessor = Substitute.For<ITransactionProcessor>();

        _transactionProcessor.Execute(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<CallOutputTracer>())
            .Returns(ci =>
            {
                CallOutputTracer tracer = ci.Arg<CallOutputTracer>();
                tracer.ReturnValue = Bytes.FromHexString("a94f5374fce5edbc8e2a8697c15331677e6ebf0b0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000010000000000000000a94f5374fce5edbc8e2a8697c15331677e6ebf0b000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000002ffffffffffffffffa94f5374fce5edbc8e2a8697c15331677e6ebf0b0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000030000000000000000a94f5374fce5edbc8e2a8697c15331677e6ebf0b000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000004ffffffffffffffffa94f5374fce5edbc8e2a8697c15331677e6ebf0b0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000050000000000000000a94f5374fce5edbc8e2a8697c15331677e6ebf0b000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000006ffffffffffffffffa94f5374fce5edbc8e2a8697c15331677e6ebf0b0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000070000000000000000a94f5374fce5edbc8e2a8697c15331677e6ebf0b000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008ffffffffffffffffa94f5374fce5edbc8e2a8697c15331677e6ebf0b0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000090000000000000000a94f5374fce5edbc8e2a8697c15331677e6ebf0b00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000affffffffffffffffa94f5374fce5edbc8e2a8697c15331677e6ebf0b00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000b0000000000000000a94f5374fce5edbc8e2a8697c15331677e6ebf0b00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000cffffffffffffffffa94f5374fce5edbc8e2a8697c15331677e6ebf0b00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000d0000000000000000a94f5374fce5edbc8e2a8697c15331677e6ebf0b00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000effffffffffffffffa94f5374fce5edbc8e2a8697c15331677e6ebf0b00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000f0000000000000000a94f5374fce5edbc8e2a8697c15331677e6ebf0b000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000010ffffffffffffffff");
                return new TransactionResult();
            });
    }


    [Test]
    public void ShouldProcessConsolidationRequest()
    {

        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.ConsolidationRequestsEnabled.Returns(true);
        spec.Eip7251ContractAddress.Returns(eip7251Account);

        Block block = Build.A.Block.TestObject;

        ConsolidationRequestsProcessor ConsolidationRequestsProcessor = new(transactionProcessor: _transactionProcessor);

        var ConsolidationRequest = new ConsolidationRequest()
        {
            SourceAddress = new Address(Bytes.FromHexString("0xa94f5374fce5edbc8e2a8697c15331677e6ebf0b")),
            SourcePubkey = Bytes.FromHexString("000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000001"),
            TargetPubkey = Bytes.FromHexString("0000000000000000a94f5374fce5edbc8e2a8697c15331677e6ebf0b0000000000000000000000000000000000000000")
        };

        var ConsolidationRequests = ConsolidationRequestsProcessor.ReadConsolidationRequests(block, _stateProvider, spec).ToList();

        Assert.That(ConsolidationRequests, Has.Count.EqualTo(10));
        ConsolidationRequest ConsolidationRequestResult = ConsolidationRequests[0];

        ConsolidationRequestResult.Should().BeEquivalentTo(ConsolidationRequest, options => options
            .Using<Memory<byte>>(ctx => ctx.Subject.Span.SequenceEqual(ctx.Expectation.Span).Should().BeTrue())
            .WhenTypeIs<Memory<byte>>());
    }
}
