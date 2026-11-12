// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test.ChainSpecStyle;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

/// <remarks>Registers the OP decoders in the global RLP registry, so it does not run alongside other tests.</remarks>
[NonParallelizable]
public class OptimismReceiptStorageTests
{
    [Test]
    public void Receipts_read_back_from_the_db_keep_the_deposit_fields()
    {
        TestSpecProvider specProvider = new(London.Instance);
        ChainSpec chainSpec = new()
        {
            Genesis = Build.A.Block.WithNumber(0).TestObject,
            Parameters = new ChainParameters(),
            Allocations = [],
            EngineChainSpecParametersProvider = new TestChainSpecParametersProvider(
                new OptimismChainSpecEngineParameters { BedrockBlockNumber = 0 })
        };
        OptimismConfig optimismConfig = new();
        INethermindApi api = Substitute.For<INethermindApi>();
        api.SpecProvider.Returns(specProvider);
        api.TxValidator.Returns(new TxValidator(specProvider.ChainId));
        new OptimismPlugin(chainSpec, optimismConfig).InitTxTypesAndRlpDecoders(api);
        try
        {
            using IContainer node = CreateNode(chainSpec, optimismConfig, specProvider);
            Transaction deposit = Build.A.Transaction.WithType(TxType.DepositTx).WithSourceHash(TestItem.KeccakA).WithSenderAddress(TestItem.AddressA).WithHash(TestItem.KeccakA).TestObject;
            Transaction regular = Build.A.Transaction.SignedAndResolved().TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(deposit, regular).WithReceiptsRoot(TestItem.KeccakB).TestObject;
            OptimismTxReceipt depositReceipt = new(Build.A.Receipt.WithTxType(TxType.DepositTx).WithGasUsedTotal(21_000).TestObject)
            {
                DepositNonce = 7,
                DepositReceiptVersion = 1
            };
            OptimismTxReceipt regularReceipt = new(Build.A.Receipt.WithGasUsedTotal(42_000).TestObject);
            node.Resolve<IReceiptStorage>().Insert(block, [depositReceipt, regularReceipt]);

            // A second node on the same receipts DB starts with an empty cache, as after a restart.
            using IContainer restarted = CreateNode(chainSpec, optimismConfig, specProvider, node.Resolve<IColumnsDb<ReceiptsColumns>>());
            TxReceipt[] stored = restarted.Resolve<IReceiptStorage>().Get(block);

            using (Assert.EnterMultipleScope())
            {
                // OptimismEthRpcModule writes the L1 fee fields only for an OptimismTxReceipt.
                Assert.That(stored, Has.All.TypeOf<OptimismTxReceipt>());
                Assert.That((stored[0] as OptimismTxReceipt)?.DepositNonce, Is.EqualTo(7));
                Assert.That((stored[0] as OptimismTxReceipt)?.DepositReceiptVersion, Is.EqualTo(1));
            }
        }
        finally
        {
            Rlp.ResetDecoders();
        }
    }

    private static IContainer CreateNode(ChainSpec chainSpec, OptimismConfig optimismConfig, ISpecProvider specProvider, IColumnsDb<ReceiptsColumns>? receiptsDb = null)
    {
        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new TestNethermindModule(chainSpec))
            .AddModule(new OptimismModule(chainSpec, optimismConfig))
            .AddSingleton(specProvider);
        if (receiptsDb is not null) builder.AddSingleton(receiptsDb);
        return builder.Build();
    }
}
