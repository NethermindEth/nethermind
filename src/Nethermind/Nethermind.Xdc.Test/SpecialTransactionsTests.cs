// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using FluentAssertions;
using FluentAssertions.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Headers;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Test.Helpers;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;

internal class SpecialTransactionsTests
{
    private bool IsTimeForOnchainSignature(IXdcReleaseSpec spec, long blockNumber)
    {
        return blockNumber % spec.MergeSignRange == 0;
    }

    private Task ProposeBatchTransferTxFrom(PrivateKey source, PrivateKey destination, UInt256 amount, int count, XdcTestBlockchain chain)
    {
        return Task.Run(() =>
        {
            (PrivateKey, PrivateKey) swap(PrivateKey a, PrivateKey b) => (b, a);

            for (int i = 0; i < count; i++)
            {
                (source, destination)  = swap(source, destination);
                CreateTransferTxFrom(source, destination, amount, chain);
            }
        });
    }

    private Transaction CreateTransferTxFrom(PrivateKey source, PrivateKey destination, UInt256 amount, XdcTestBlockchain chain)
    {
        Transaction tx = Build.A.Transaction
            .WithSenderAddress(source.Address)
            .WithTo(destination.Address)
            .WithValue(amount)
            .WithType(TxType.Legacy)
            .TestObject;

        var signer = new Signer(chain.SpecProvider.ChainId, source, NullLogManager.Instance);
        signer.Sign(tx);

        tx.Hash = tx.CalculateHash();

        var result = chain.TxPool.SubmitTx(tx, TxHandlingOptions.None);

        return tx;
    }

    private PrivateKey[] FilledAccounts(XdcTestBlockchain chain)
    {
        var genesisSpec = chain.SpecProvider.GenesisSpec as XdcReleaseSpec;
        var pks = chain.MasterNodeCandidates
            .Where(k => genesisSpec!.GenesisMasterNodes.Contains(k.Address));
        return pks.ToArray();
    }

    [Test]
    public async Task Special_Tx_Is_Dispatched_On_MergeSignRange_Block()
    {
        var blockChain = await XdcTestBlockchain.Create(1, true);

        var mergeSignBlockRange = 5;

        blockChain.ChangeReleaseSpec((spec) =>
        {
            spec.MergeSignRange = mergeSignBlockRange;
            spec.IsEip1559Enabled = false;
        });

        blockChain.StartHotStuffModule();

        XdcBlockHeader? head = blockChain.BlockTree.Head!.Header as XdcBlockHeader;
        do
        {
            await blockChain.TriggerAndSimulateBlockProposalAndVoting();
            await Task.Delay(blockChain.SpecProvider.GetXdcSpec(head!).MinePeriod.Seconds()); // to avoid tight loop
            head = (XdcBlockHeader)blockChain.BlockTree.Head!.Header;
        }
        while (!IsTimeForOnchainSignature(blockChain.SpecProvider.GetXdcSpec(head), head.Number - 1));

        

        Assert.That(blockChain.BlockTree.Head.Number, Is.EqualTo(mergeSignBlockRange + 1));

        await Task.Delay(((XdcReleaseSpec)blockChain.SpecProvider.GetFinalSpec()).MinePeriod.Seconds()); // to avoid tight loop

        Transaction[] pendingTxs = blockChain.TxPool.GetPendingTransactions();

        var specialTxs = pendingTxs.Where(r => r.To == blockChain.SpecProvider.GetXdcSpec(head).BlockSignersAddress
                                            || r.To == blockChain.SpecProvider.GetXdcSpec(head).RandomizeSMCBinary);

        Assert.That(specialTxs, Is.Not.Empty);

        var specialTx = specialTxs.First();

        var blockTarget = (long)(new UInt256(specialTx.Data.Span.Slice(4, 32), true));

        Assert.That(blockTarget, Is.EqualTo(mergeSignBlockRange));
    }

    [Test]
    public async Task Special_Tx_Is_Not_Dispatched_Outside_MergeSignRange_Block()
    {
        var blockChain = await XdcTestBlockchain.Create(1, true);

        var mergeSignBlockRange = 5;

        blockChain.ChangeReleaseSpec((spec) =>
        {
            spec.MergeSignRange = mergeSignBlockRange;
            spec.IsEip1559Enabled = false;
        });

        blockChain.StartHotStuffModule();

        XdcBlockHeader? head = blockChain.BlockTree.Head!.Header as XdcBlockHeader;
        do
        {
            await blockChain.TriggerAndSimulateBlockProposalAndVoting();
            await Task.Delay(blockChain.SpecProvider.GetXdcSpec(head!).MinePeriod.Seconds()); // to avoid tight loop
            head = (XdcBlockHeader)blockChain.BlockTree.Head!.Header;
        }
        while (!IsTimeForOnchainSignature(blockChain.SpecProvider.GetXdcSpec(head), head.Number + 1));

        // await blockChain.StopHotStuffModule();

        await Task.Delay(((XdcReleaseSpec)blockChain.SpecProvider.GetFinalSpec()).MinePeriod.Seconds()); // to avoid tight loop

        var receipts = blockChain.TxPool.GetPendingTransactions();

        receipts.Any(r => r.To == blockChain.SpecProvider.GetXdcSpec(head).BlockSignersAddress
                       || r.To == blockChain.SpecProvider.GetXdcSpec(head).RandomizeSMCBinary).Should().BeFalse();
    }

    [Test]
    public async Task Special_Tx_Is_Executed_Before_Normal_Txs()
    {
        var blockChain = await XdcTestBlockchain.Create(1, true);

        var mergeSignBlockRange = 5;

        blockChain.ChangeReleaseSpec((spec) =>
        {
            spec.MergeSignRange = mergeSignBlockRange;
            spec.IsEip1559Enabled = false;
        });

        blockChain.StartHotStuffModule();

        XdcBlockHeader? head = blockChain.BlockTree.Head!.Header as XdcBlockHeader;
        var spec = blockChain.SpecProvider.GetXdcSpec(head!);

        var random = new Random();

        var accounts = FilledAccounts(blockChain);

        for (int i = 1; i < spec.MergeSignRange + 2; i++)
        {
            if (head!.Number == mergeSignBlockRange + 1)
            {
                var source = accounts.ElementAt(random.Next() % accounts.Length);
                var dest = accounts.Except([source]).ElementAt(random.Next() % (accounts.Length - 1));
                await ProposeBatchTransferTxFrom(source, dest, 1, 2, blockChain);
            }

            await blockChain.TriggerAndSimulateBlockProposalAndVoting();
            await Task.Delay(blockChain.SpecProvider.GetXdcSpec(head!).MinePeriod.Seconds()); // to avoid tight loop
            head = (XdcBlockHeader)blockChain.BlockTree.Head!.Header;
        }

        var block = (XdcBlockHeader)blockChain.BlockTree.Head.Header;
        spec = blockChain.SpecProvider.GetXdcSpec(block!);

        var receipts = blockChain.ReceiptStorage.Get(block.Hash!);

        Assert.That(receipts, Is.Not.Empty);
        Assert.That(receipts.Length, Is.GreaterThan(1));

        bool onlyEncounteredSpecialTx = true;
        foreach (var transaction in receipts)
        {
            if (transaction.Recipient == spec.BlockSignersAddress || transaction.Recipient == spec.RandomizeSMCBinary)
            {
                if (!onlyEncounteredSpecialTx)
                {
                    // we encountered a normal transaction before so special txs are not lumped at the start
                    Assert.Fail();
                }
            }
            else
            {
                onlyEncounteredSpecialTx = false;
            }
        }

        Assert.Pass();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Tx_With_With_BlackListed_Sender_Fails_Validation(bool blackListingActivated)
    {
        var blockChain = await XdcTestBlockchain.Create(5, false);
        blockChain.ChangeReleaseSpec((spec) =>
        {
            spec.BlackListedAddresses = [blockChain.Signer.Address];
            spec.IsEip1559Enabled = false;
            spec.BlackListHFNumber = blackListingActivated ? 0 : long.MaxValue;
        });

        var moqVm = new VirtualMachine(new BlockhashProvider(new BlockhashCache(blockChain.Container.Resolve<IHeaderFinder>(), NullLogManager.Instance), blockChain.WorldStateManager.GlobalWorldState, NullLogManager.Instance), blockChain.SpecProvider, NullLogManager.Instance);

        var transactionProcessor = new XdcTransactionProcessor(BlobBaseFeeCalculator.Instance, blockChain.SpecProvider, blockChain.WorldStateManager.GlobalWorldState, moqVm, NSubstitute.Substitute.For<ICodeInfoRepository>(), NullLogManager.Instance);


        XdcBlockHeader head = (XdcBlockHeader)blockChain.BlockTree.Head!.Header!;
        XdcReleaseSpec spec = (XdcReleaseSpec)blockChain.SpecProvider.GetXdcSpec(head);


        moqVm.SetBlockExecutionContext(new BlockExecutionContext(head, spec));

        var txSign = ContractsUtils.CreateTxSign((UInt256)head.Number, head.Hash!, blockChain.TxPool.GetLatestPendingNonce(TestItem.AddressA), spec.BlockSignersAddress, blockChain.Signer.Address);
        await blockChain.Signer.Sign(txSign);

        TransactionResult? result = null;

        try
        {
            result = transactionProcessor.Execute(txSign, NullTxTracer.Instance);
        }
        catch
        {
            result = TransactionResult.Ok;
        }

        if (blackListingActivated)
        {
            result.Value.Error.Should().Be(TransactionResult.ErrorType.ContainsBlacklistedAddress);
        }
        else
        {
            result.Value.Error.Should().NotBe(TransactionResult.ErrorType.ContainsBlacklistedAddress);
        }
    }


    [TestCase(false)]
    [TestCase(true)]
    public async Task Tx_With_With_BlackListed_Receiver_Fails_Validation(bool blackListingActivated)
    {

        var blockChain = await XdcTestBlockchain.Create(5, false);
        blockChain.ChangeReleaseSpec((spec) =>
        {
            spec.BlackListedAddresses = [TestItem.AddressA];
            spec.IsEip1559Enabled = false;
            spec.BlackListHFNumber = blackListingActivated ? 0 : long.MaxValue;
        });
        var moqVm = new VirtualMachine(new BlockhashProvider(new BlockhashCache(blockChain.Container.Resolve<IHeaderFinder>(), NullLogManager.Instance), blockChain.WorldStateManager.GlobalWorldState, NullLogManager.Instance), blockChain.SpecProvider, NullLogManager.Instance);

        var transactionProcessor = new XdcTransactionProcessor(BlobBaseFeeCalculator.Instance, blockChain.SpecProvider, blockChain.WorldStateManager.GlobalWorldState, moqVm, NSubstitute.Substitute.For<ICodeInfoRepository>(), NullLogManager.Instance);


        XdcBlockHeader head = (XdcBlockHeader)blockChain.BlockTree.Head!.Header!;
        XdcReleaseSpec spec = (XdcReleaseSpec)blockChain.SpecProvider.GetXdcSpec(head);


        moqVm.SetBlockExecutionContext(new BlockExecutionContext(head, spec));

        Transaction tx = Build.A.Transaction.WithSenderAddress(blockChain.Signer.Address).WithTo(TestItem.AddressA).TestObject;
        await blockChain.Signer.Sign(tx);

        TransactionResult? result = null;

        try
        {
            result = transactionProcessor.Execute(tx, NullTxTracer.Instance);
        }
        catch
        {
            result = TransactionResult.Ok;
        }

        if (blackListingActivated)
        {
            result.Value.Error.Should().Be(TransactionResult.ErrorType.ContainsBlacklistedAddress);
        }
        else
        {
            result.Value.Error.Should().NotBe(TransactionResult.ErrorType.ContainsBlacklistedAddress);
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Malformed_WrongLenght_SpecialTx_Fails_Validation(bool isSpecialTx)
    {
        var blockChain = await XdcTestBlockchain.Create(5, false);
        blockChain.ChangeReleaseSpec((spec) =>
        {
            spec.IsEip1559Enabled = false;
        });

        var moqVm = new VirtualMachine(new BlockhashProvider(new BlockhashCache(blockChain.Container.Resolve<IHeaderFinder>(), NullLogManager.Instance), blockChain.WorldStateManager.GlobalWorldState, NullLogManager.Instance), blockChain.SpecProvider, NullLogManager.Instance);

        var transactionProcessor = new XdcTransactionProcessor(BlobBaseFeeCalculator.Instance, blockChain.SpecProvider, blockChain.WorldStateManager.GlobalWorldState, moqVm, NSubstitute.Substitute.For<ICodeInfoRepository>(), NullLogManager.Instance);


        XdcBlockHeader head = (XdcBlockHeader)blockChain.BlockTree.Head!.Header!;
        XdcReleaseSpec spec = (XdcReleaseSpec)blockChain.SpecProvider.GetXdcSpec(head);


        moqVm.SetBlockExecutionContext(new BlockExecutionContext(head, spec));

        Transaction? tx = null;

        if (isSpecialTx)
        {
            tx = ContractsUtils.CreateTxSign((UInt256)head.Number, head.Hash!, blockChain.TxPool.GetLatestPendingNonce(TestItem.AddressA), spec.BlockSignersAddress, blockChain.Signer.Address);
        }
        else
        {
            tx = Build.A.Transaction.WithSenderAddress(blockChain.Signer.Address).WithTo(TestItem.AddressA).TestObject;
        }

        // damage the data field in the tx
        tx.Data = Enumerable.Range(0, 48).Select(i => (byte)i).ToArray();

        await blockChain.Signer.Sign(tx);

        TransactionResult? result = null;

        try
        {
            result = transactionProcessor.Execute(tx, NullTxTracer.Instance);
        }
        catch
        {
            result = TransactionResult.Ok;
        }

        if (isSpecialTx)
        {
            result.Value.Error.Should().Be(TransactionResult.ErrorType.MalformedTransaction);
        }
        else
        {
            result.Value.Error.Should().NotBe(TransactionResult.ErrorType.MalformedTransaction);
        }
    }

    public async Task Malformed_WrongBlockNumber_BlockLessThanCurrent_SpecialTx_Fails_Validation()
    {
        var blockChain = await XdcTestBlockchain.Create(5, false);
        blockChain.ChangeReleaseSpec((spec) =>
        {
            spec.IsEip1559Enabled = false;
        });

        var moqVm = new VirtualMachine(new BlockhashProvider(new BlockhashCache(blockChain.Container.Resolve<IHeaderFinder>(), NullLogManager.Instance), blockChain.WorldStateManager.GlobalWorldState, NullLogManager.Instance), blockChain.SpecProvider, NullLogManager.Instance);

        var transactionProcessor = new XdcTransactionProcessor(BlobBaseFeeCalculator.Instance, blockChain.SpecProvider, blockChain.WorldStateManager.GlobalWorldState, moqVm, NSubstitute.Substitute.For<ICodeInfoRepository>(), NullLogManager.Instance);


        XdcBlockHeader head = (XdcBlockHeader)blockChain.BlockTree.Head!.Header!;
        XdcReleaseSpec spec = (XdcReleaseSpec)blockChain.SpecProvider.GetXdcSpec(head);


        moqVm.SetBlockExecutionContext(new BlockExecutionContext(head, spec));


        var blockNumber = head.Number - 1;
        Transaction? tx = ContractsUtils.CreateTxSign((UInt256)blockNumber, head.Hash!, blockChain.TxPool.GetLatestPendingNonce(TestItem.AddressA), spec.BlockSignersAddress, blockChain.Signer.Address);

        await blockChain.Signer.Sign(tx);

        TransactionResult? result = transactionProcessor.Execute(tx, NullTxTracer.Instance);

        result.Value.Error.Should().NotBe(TransactionResult.ErrorType.MalformedTransaction);
    }

    public async Task Malformed_WrongBlockNumber_BlockEqualToCurrent_SpecialTx_Fails_Validation()
    {
        var blockChain = await XdcTestBlockchain.Create(5, false);
        blockChain.ChangeReleaseSpec((spec) =>
        {
            spec.IsEip1559Enabled = false;
        });

        var moqVm = new VirtualMachine(new BlockhashProvider(new BlockhashCache(blockChain.Container.Resolve<IHeaderFinder>(), NullLogManager.Instance), blockChain.WorldStateManager.GlobalWorldState, NullLogManager.Instance), blockChain.SpecProvider, NullLogManager.Instance);

        var transactionProcessor = new XdcTransactionProcessor(BlobBaseFeeCalculator.Instance, blockChain.SpecProvider, blockChain.WorldStateManager.GlobalWorldState, moqVm, NSubstitute.Substitute.For<ICodeInfoRepository>(), NullLogManager.Instance);


        XdcBlockHeader head = (XdcBlockHeader)blockChain.BlockTree.Head!.Header!;
        XdcReleaseSpec spec = (XdcReleaseSpec)blockChain.SpecProvider.GetXdcSpec(head);


        moqVm.SetBlockExecutionContext(new BlockExecutionContext(head, spec));


        var blockNumber = head.Number;
        Transaction? tx = ContractsUtils.CreateTxSign((UInt256)blockNumber, head.Hash!, blockChain.TxPool.GetLatestPendingNonce(TestItem.AddressA), spec.BlockSignersAddress, blockChain.Signer.Address);

        await blockChain.Signer.Sign(tx);

        TransactionResult? result = transactionProcessor.Execute(tx, NullTxTracer.Instance);

        result.Value.Error.Should().Be(TransactionResult.ErrorType.MalformedTransaction);
    }

    public async Task Malformed_WrongBlockNumber_BlockBiggerThanCurrent_SpecialTx_Fails_Validation()
    {
        var blockChain = await XdcTestBlockchain.Create(5, false);
        blockChain.ChangeReleaseSpec((spec) =>
        {
            spec.IsEip1559Enabled = false;
        });

        var moqVm = new VirtualMachine(new BlockhashProvider(new BlockhashCache(blockChain.Container.Resolve<IHeaderFinder>(), NullLogManager.Instance), blockChain.WorldStateManager.GlobalWorldState, NullLogManager.Instance), blockChain.SpecProvider, NullLogManager.Instance);

        var transactionProcessor = new XdcTransactionProcessor(BlobBaseFeeCalculator.Instance, blockChain.SpecProvider, blockChain.WorldStateManager.GlobalWorldState, moqVm, NSubstitute.Substitute.For<ICodeInfoRepository>(), NullLogManager.Instance);


        XdcBlockHeader head = (XdcBlockHeader)blockChain.BlockTree.Head!.Header!;
        XdcReleaseSpec spec = (XdcReleaseSpec)blockChain.SpecProvider.GetXdcSpec(head);


        moqVm.SetBlockExecutionContext(new BlockExecutionContext(head, spec));


        var blockNumber = head.Number + 1;
        Transaction? tx = ContractsUtils.CreateTxSign((UInt256)blockNumber, head.Hash!, blockChain.TxPool.GetLatestPendingNonce(TestItem.AddressA), spec.BlockSignersAddress, blockChain.Signer.Address);

        await blockChain.Signer.Sign(tx);

        TransactionResult? result = transactionProcessor.Execute(tx, NullTxTracer.Instance);

        result.Value.Error.Should().Be(TransactionResult.ErrorType.MalformedTransaction);
    }
}
