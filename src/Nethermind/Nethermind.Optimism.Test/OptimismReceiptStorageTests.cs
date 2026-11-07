// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
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
        // As OptimismPlugin.InitTxTypesAndRlpDecoders does on an OP node.
        TxDecoder.Instance.RegisterDecoder(new OptimismTxDecoder());
        Rlp.RegisterDecoders(typeof(OptimismReceiptMessageDecoder).Assembly, true);
        try
        {
            TestSpecProvider specProvider = new(London.Instance);
            Transaction deposit = Build.A.Transaction.WithType(TxType.DepositTx).WithSourceHash(TestItem.KeccakA).WithSenderAddress(TestItem.AddressA).WithHash(TestItem.KeccakA).TestObject;
            Transaction regular = Build.A.Transaction.SignedAndResolved().TestObject;
            Block block = Build.A.Block.WithNumber(1).WithTransactions(deposit, regular).WithReceiptsRoot(TestItem.KeccakB).TestObject;
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.FindBlock(block.Hash!).Returns(block);
            blockTree.FindBlock(block.Number).Returns(block);
            blockTree.FindHeader(block.Number).Returns(block.Header);
            blockTree.FindBlockHash(block.Number).Returns(block.Hash);
            IBlockStore blockStore = Substitute.For<IBlockStore>();
            blockStore.GetReceiptRecoveryBlock(block.Number, block.Hash!).Returns(new ReceiptRecoveryBlock(block));
            using TestMemColumnsDb<ReceiptsColumns> receiptsDb = new();
            receiptsDb.GetColumnDb(ReceiptsColumns.Blocks).Set(Keccak.Zero, Array.Empty<byte>());
            PersistentReceiptStorage CreateStorage() => new(
                receiptsDb,
                specProvider,
                new ReceiptsRecovery(new EthereumEcdsa(specProvider.ChainId), specProvider),
                blockTree,
                blockStore,
                new ReceiptConfig())
            { MigratedBlockNumber = 0 };

            OptimismTxReceipt depositReceipt = new(Build.A.Receipt.WithTxType(TxType.DepositTx).WithGasUsedTotal(21_000).TestObject)
            {
                DepositNonce = 7,
                DepositReceiptVersion = 1
            };
            OptimismTxReceipt regularReceipt = new(Build.A.Receipt.WithGasUsedTotal(42_000).TestObject);
            CreateStorage().Insert(block, [depositReceipt, regularReceipt]);

            // A new instance has an empty cache, as after a restart.
            TxReceipt[] stored = CreateStorage().Get(block);

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
}
