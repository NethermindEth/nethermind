// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Config;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Sequencer;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class LiveBlockProducerTests
{
    private static readonly Address Beneficiary = TestItem.AddressF;

    private BasicTestBlockchain _chain = null!;
    private EezSettlementContext _context = null!;
    private LiveBlockProducer _producer = null!;

    [SetUp]
    public async Task SetUp()
    {
        _chain = await BasicTestBlockchain.Create(static builder => builder
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Prague.Instance) { AllowTestChainOverride = false })
            .AddModule(new EezModule(new EezConfig())));
        _context = new EezSettlementContext(1, _chain.SpecProvider.ChainId, Address.Zero, default, 2, _chain.BlockTree.Head!.GasLimit);
        _producer = new LiveBlockProducer(_chain.Container.Resolve<IBlockProducerEnvFactory>(), _chain.SpecProvider, _context, Beneficiary);
    }

    [TearDown]
    public void TearDown() => _chain.Dispose();

    [Test]
    public void Produce_PendingTransaction_IsIncludedUnderTheDerivedHeader()
    {
        BlockHeader parent = _chain.BlockTree.Head!.Header;
        Transaction transfer = Transfer(nonce: 0);
        Assert.That(_chain.TxPool.SubmitTx(transfer, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted), "precondition: the pool takes the transfer");

        Block block = _producer.Produce(parent, CancellationToken.None)!;

        Assert.That(block.Transactions.Select(static t => t.Hash), Is.EqualTo(new[] { transfer.Hash }), "the pool's transaction is in the block");
        Assert.That(DerivedHeader.Mismatch(block, parent, _chain.SpecProvider.GetSpec(block.Header), _context), Is.Null,
            "every header field derivation decides is the one it rebuilds");
        Assert.That(block.Beneficiary, Is.EqualTo(Beneficiary), "the beneficiary the DA carries is the sequencer's");
        Assert.That(block.ExtraData, Is.Empty, "the sequencer publishes no extra data");
        Assert.That(block.Hash, Is.EqualTo(block.Header.CalculateHash()), "the block is sealed with its final header");
    }

    [Test]
    public void Produce_EmptyPool_ProducesAnEmptyDerivedBlock()
    {
        BlockHeader parent = _chain.BlockTree.Head!.Header;

        Block block = _producer.Produce(parent, CancellationToken.None)!;

        Assert.That(block.Transactions, Is.Empty, "a slot's blocks are produced on the clock whether or not the pool has anything");
        Assert.That(DerivedHeader.Mismatch(block, parent, _chain.SpecProvider.GetSpec(block.Header), _context), Is.Null, "an empty block still has the derived header");
    }

    private static Transaction Transfer(ulong nonce) =>
        Build.A.Transaction.WithTo(TestItem.AddressC).WithValue(1).WithNonce(nonce).WithGasLimit(21_000).WithMaxFeePerGas(20.GWei)
            .WithType(TxType.EIP1559).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
}
