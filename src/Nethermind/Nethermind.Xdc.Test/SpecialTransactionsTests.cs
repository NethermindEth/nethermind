// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using FluentAssertions;
using FluentAssertions.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Tracing;
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
using Nethermind.Evm.Tracing.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.Spec;
using Nethermind.Xdc.Test.Helpers;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using System;
using System.Collections;
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
                (source, destination) = swap(source, destination);
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

        chain.TxPool.SubmitTx(tx, TxHandlingOptions.None);

        return tx;
    }

    private PrivateKey[] FilledAccounts(XdcTestBlockchain chain)
    {
        var genesisSpec = chain.SpecProvider.GenesisSpec as XdcReleaseSpec;
        var pks = chain.MasterNodeCandidates
            .Where(k => genesisSpec!.GenesisMasterNodes.Contains(k.Address));
        return pks.ToArray();
    }


    [TestCase(false)]
    [TestCase(true)]
    public async Task SignTx_Is_Dispatched_On_MergeSignRange_Block(bool enableEip1559)
    {
        var blockChain = await XdcTestBlockchain.Create(1, true);

        var mergeSignBlockRange = 5;

        blockChain.ChangeReleaseSpec((spec) =>
        {
            spec.MergeSignRange = mergeSignBlockRange;
            spec.IsEip1559Enabled = enableEip1559;
        });

        blockChain.StartHotStuffModule();

        XdcBlockHeader? head = blockChain.BlockTree.Head!.Header as XdcBlockHeader;
        do
        {
            await blockChain.TriggerAndSimulateBlockProposalAndVoting();
            head = (XdcBlockHeader)blockChain.BlockTree.Head!.Header;
        }
        while (!IsTimeForOnchainSignature(blockChain.SpecProvider.GetXdcSpec(head), head.Number - 1));

        Assert.That(blockChain.BlockTree.Head.Number, Is.EqualTo(mergeSignBlockRange + 1));

        Transaction[] pendingTxs = blockChain.TxPool.GetPendingTransactions();

        var spec = (XdcReleaseSpec)blockChain.SpecProvider.GetFinalSpec();
        var specialTxs = pendingTxs.Where(r => r.To == spec.BlockSignerContract);

        Assert.That(specialTxs, Is.Not.Empty);

        var specialTx = specialTxs.First();

        var blockTarget = (long)(new UInt256(specialTx.Data.Span.Slice(4, 32), true));

        Assert.That(blockTarget, Is.EqualTo(mergeSignBlockRange));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task SignTx_Is_Not_Dispatched_Outside_MergeSignRange_Block(bool enableEip1559)
    {
        var blockChain = await XdcTestBlockchain.Create(1, true);

        var mergeSignBlockRange = 5;

        blockChain.ChangeReleaseSpec((spec) =>
        {
            spec.MergeSignRange = mergeSignBlockRange;
            spec.IsEip1559Enabled = enableEip1559;
        });

        blockChain.StartHotStuffModule();

        XdcBlockHeader? head = blockChain.BlockTree.Head!.Header as XdcBlockHeader;
        do
        {
            await blockChain.TriggerAndSimulateBlockProposalAndVoting();
            head = (XdcBlockHeader)blockChain.BlockTree.Head!.Header;
        }
        while (!IsTimeForOnchainSignature(blockChain.SpecProvider.GetXdcSpec(head), head.Number + 1));

        var receipts = blockChain.TxPool.GetPendingTransactions();

        var spec = (XdcReleaseSpec)blockChain.SpecProvider.GetFinalSpec();
        receipts.Any(r => r.To == spec.BlockSignerContract
                       || r.To == spec.RandomizeSMCBinary).Should().BeFalse();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Special_Tx_Is_Executed_Before_Normal_Txs(bool enableEip1559)
    {
        var blockChain = await XdcTestBlockchain.Create(1, true);

        var mergeSignBlockRange = 5;

        blockChain.ChangeReleaseSpec((spec) =>
        {
            spec.MergeSignRange = mergeSignBlockRange;
            spec.IsEip1559Enabled = enableEip1559;
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
            if (transaction.Recipient == spec.BlockSignerContract || transaction.Recipient == spec.RandomizeSMCBinary)
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

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, false)]
    public async Task Tx_With_With_BlackListed_Sender_Fails_Validation(bool blackListingActivated, bool enableEip1559)
    {
        var blockChain = await XdcTestBlockchain.Create(5, false);
        blockChain.ChangeReleaseSpec((spec) =>
        {
            spec.BlackListedAddresses = [blockChain.Signer.Address];
            spec.IsEip1559Enabled = enableEip1559;
            spec.IsBlackListingEnabled = blackListingActivated;
        });

        var moqVm = new VirtualMachine(new BlockhashProvider(new BlockhashCache(blockChain.Container.Resolve<IHeaderFinder>(), NullLogManager.Instance), blockChain.MainWorldState, NullLogManager.Instance), blockChain.SpecProvider, NullLogManager.Instance);

        var transactionProcessor = new XdcTransactionProcessor(BlobBaseFeeCalculator.Instance, blockChain.SpecProvider, blockChain.MainWorldState, moqVm, NSubstitute.Substitute.For<ICodeInfoRepository>(), NullLogManager.Instance);


        XdcBlockHeader head = (XdcBlockHeader)blockChain.BlockTree.Head!.Header!;
        XdcReleaseSpec spec = (XdcReleaseSpec)blockChain.SpecProvider.GetXdcSpec(head);

        blockChain.MainWorldState.BeginScope(head);

        moqVm.SetBlockExecutionContext(new BlockExecutionContext(head, spec));

        var txSign = SignTransactionManager.CreateTxSign((UInt256)head.Number, head.Hash!, blockChain.TxPool.GetLatestPendingNonce(TestItem.AddressA), spec.BlockSignerContract, blockChain.Signer.Address);
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
            result.Value.Error.Should().Be(XdcTransactionResult.ContainsBlacklistedAddressError);
        }
        else
        {
            result.Value.Error.Should().NotBe(XdcTransactionResult.ContainsBlacklistedAddressError);
        }
    }


    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task Tx_With_With_BlackListed_Receiver_Fails_Validation(bool blackListingActivated, bool enableEip1559)
    {

        var blockChain = await XdcTestBlockchain.Create(5, false);
        blockChain.ChangeReleaseSpec((spec) =>
        {
            spec.BlackListedAddresses = [TestItem.AddressA];
            spec.IsEip1559Enabled = enableEip1559;
            spec.IsBlackListingEnabled = blackListingActivated;
        });
        var moqVm = new VirtualMachine(new BlockhashProvider(new BlockhashCache(blockChain.Container.Resolve<IHeaderFinder>(), NullLogManager.Instance), blockChain.MainWorldState, NullLogManager.Instance), blockChain.SpecProvider, NullLogManager.Instance);

        var transactionProcessor = new XdcTransactionProcessor(BlobBaseFeeCalculator.Instance, blockChain.SpecProvider, blockChain.MainWorldState, moqVm, NSubstitute.Substitute.For<ICodeInfoRepository>(), NullLogManager.Instance);


        XdcBlockHeader head = (XdcBlockHeader)blockChain.BlockTree.Head!.Header!;
        XdcReleaseSpec spec = (XdcReleaseSpec)blockChain.SpecProvider.GetXdcSpec(head);

        blockChain.MainWorldState.BeginScope(head);

        moqVm.SetBlockExecutionContext(new BlockExecutionContext(head, spec));

        var nonce = blockChain.ReadOnlyState.GetNonce(blockChain.Signer.Address);

        Transaction tx = Build.A.Transaction
            .WithNonce(nonce)
            .WithSenderAddress(blockChain.Signer.Address)
            .WithTo(TestItem.AddressA).TestObject;
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
            result.Value.Error.Should().Be(XdcTransactionResult.ContainsBlacklistedAddressError);
        }
        else
        {
            result.Value.Error.Should().NotBe(XdcTransactionResult.ContainsBlacklistedAddressError);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Malformed_WrongLength_SpecialTx_Fails_Validation(bool enableEip1559)
    {
        var blockChain = await XdcTestBlockchain.Create(5, false);
        blockChain.ChangeReleaseSpec((spec) =>
        {
            spec.IsEip1559Enabled = enableEip1559;
        });

        var moqVm = new VirtualMachine(new BlockhashProvider(new BlockhashCache(blockChain.Container.Resolve<IHeaderFinder>(), NullLogManager.Instance), blockChain.MainWorldState, NullLogManager.Instance), blockChain.SpecProvider, NullLogManager.Instance);

        var transactionProcessor = new XdcTransactionProcessor(BlobBaseFeeCalculator.Instance, blockChain.SpecProvider, blockChain.MainWorldState, moqVm, NSubstitute.Substitute.For<ICodeInfoRepository>(), NullLogManager.Instance);

        XdcBlockHeader head = (XdcBlockHeader)blockChain.BlockTree.Head!.Header!;
        XdcReleaseSpec spec = (XdcReleaseSpec)blockChain.SpecProvider.GetXdcSpec(head);

        blockChain.MainWorldState.BeginScope(head);

        moqVm.SetBlockExecutionContext(new BlockExecutionContext(head, spec));

        Transaction? tx = SignTransactionManager.CreateTxSign((UInt256)head.Number, head.Hash!, blockChain.TxPool.GetLatestPendingNonce(blockChain.Signer.Address), spec.BlockSignerContract, blockChain.Signer.Address);

        // damage the data field in the tx
        tx.Data = Enumerable.Range(0, 48).Select(i => (byte)i).ToArray();

        await blockChain.Signer.Sign(tx);

        var result = blockChain.TxPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);

        Assert.That(result, Is.EqualTo(AcceptTxResult.Invalid));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Malformed_SenderNonceLesserThanTxNonce_SignTx_Fails_Validation(bool enableEip1559)
    {
        var blockChain = await XdcTestBlockchain.Create(5, false);
        blockChain.ChangeReleaseSpec((spec) =>
        {
            spec.IsEip1559Enabled = enableEip1559;
        });

        var moqVm = new VirtualMachine(new BlockhashProvider(new BlockhashCache(blockChain.Container.Resolve<IHeaderFinder>(), NullLogManager.Instance), blockChain.MainWorldState, NullLogManager.Instance), blockChain.SpecProvider, NullLogManager.Instance);

        var transactionProcessor = new XdcTransactionProcessor(BlobBaseFeeCalculator.Instance, blockChain.SpecProvider, blockChain.MainWorldState, moqVm, NSubstitute.Substitute.For<ICodeInfoRepository>(), NullLogManager.Instance);


        XdcBlockHeader head = (XdcBlockHeader)blockChain.BlockTree.Head!.Header!;
        XdcReleaseSpec spec = (XdcReleaseSpec)blockChain.SpecProvider.GetXdcSpec(head);

        blockChain.MainWorldState.BeginScope(head);

        moqVm.SetBlockExecutionContext(new BlockExecutionContext(head, spec));


        blockChain.MainWorldState.IncrementNonce(blockChain.Signer.Address);
        var nonce = blockChain.MainWorldState.GetNonce(blockChain.Signer.Address);


        Transaction txWithSmallerNonce = SignTransactionManager.CreateTxSign((UInt256)head.Number, head.Hash!, nonce - 1, spec.BlockSignerContract, blockChain.Signer.Address);

        // damage the data field in the tx
        txWithSmallerNonce.Data = Enumerable.Range(0, 48).Select(i => (byte)i).ToArray();

        await blockChain.Signer.Sign(txWithSmallerNonce);

        TransactionResult? result = null;

        try
        {
            result = transactionProcessor.Execute(txWithSmallerNonce, NullTxTracer.Instance);
        }
        catch
        {
            result = TransactionResult.Ok;
        }

        result.Value.Error.Should().Be(XdcTransactionResult.NonceTooLowError);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Malformed_SenderNonceBiggerLesserThanTxNonce_SignTx_Fails_Validation(bool enableEip1559)
    {
        var blockChain = await XdcTestBlockchain.Create(5, false);
        blockChain.ChangeReleaseSpec((spec) =>
        {
            spec.IsEip1559Enabled = enableEip1559;
        });

        var moqVm = new VirtualMachine(new BlockhashProvider(new BlockhashCache(blockChain.Container.Resolve<IHeaderFinder>(), NullLogManager.Instance), blockChain.MainWorldState, NullLogManager.Instance), blockChain.SpecProvider, NullLogManager.Instance);

        var transactionProcessor = new XdcTransactionProcessor(BlobBaseFeeCalculator.Instance, blockChain.SpecProvider, blockChain.MainWorldState, moqVm, NSubstitute.Substitute.For<ICodeInfoRepository>(), NullLogManager.Instance);


        XdcBlockHeader head = (XdcBlockHeader)blockChain.BlockTree.Head!.Header!;
        XdcReleaseSpec spec = (XdcReleaseSpec)blockChain.SpecProvider.GetXdcSpec(head);

        blockChain.MainWorldState.BeginScope(head);

        moqVm.SetBlockExecutionContext(new BlockExecutionContext(head, spec));


        blockChain.MainWorldState.IncrementNonce(blockChain.Signer.Address);
        var nonce = blockChain.MainWorldState.GetNonce(blockChain.Signer.Address);


        Transaction txWithBiggerNonce = SignTransactionManager.CreateTxSign((UInt256)head.Number, head.Hash!, nonce + 1, spec.BlockSignerContract, blockChain.Signer.Address);

        // damage the data field in the tx
        txWithBiggerNonce.Data = Enumerable.Range(0, 48).Select(i => (byte)i).ToArray();

        await blockChain.Signer.Sign(txWithBiggerNonce);

        TransactionResult? result = null;

        try
        {
            result = transactionProcessor.Execute(txWithBiggerNonce, NullTxTracer.Instance);
        }
        catch
        {
            result = TransactionResult.Ok;
        }

        result.Value.Error.Should().Be(XdcTransactionResult.NonceTooHighError);
    }


    [TestCase(true)]
    [TestCase(false)]
    public async Task Malformed_SenderNonceEqualLesserThanTxNonce_SignTx_Fails_Validation(bool enableEip1559)
    {
        var blockChain = await XdcTestBlockchain.Create(5, false);
        blockChain.ChangeReleaseSpec((spec) =>
        {
            spec.IsEip1559Enabled = false;
        });

        var moqVm = new VirtualMachine(new BlockhashProvider(new BlockhashCache(blockChain.Container.Resolve<IHeaderFinder>(), NullLogManager.Instance), blockChain.MainWorldState, NullLogManager.Instance), blockChain.SpecProvider, NullLogManager.Instance);

        var transactionProcessor = new XdcTransactionProcessor(BlobBaseFeeCalculator.Instance, blockChain.SpecProvider, blockChain.MainWorldState, moqVm, NSubstitute.Substitute.For<ICodeInfoRepository>(), NullLogManager.Instance);


        XdcBlockHeader head = (XdcBlockHeader)blockChain.BlockTree.Head!.Header!;
        XdcReleaseSpec spec = (XdcReleaseSpec)blockChain.SpecProvider.GetXdcSpec(head);

        blockChain.MainWorldState.BeginScope(head);

        moqVm.SetBlockExecutionContext(new BlockExecutionContext(head, spec));


        blockChain.MainWorldState.IncrementNonce(blockChain.Signer.Address);
        var nonce = blockChain.MainWorldState.GetNonce(blockChain.Signer.Address);


        Transaction validNonceTx = SignTransactionManager.CreateTxSign((UInt256)head.Number, head.Hash!, nonce, spec.BlockSignerContract, blockChain.Signer.Address);

        // damage the data field in the tx
        validNonceTx.Data = Enumerable.Range(0, 48).Select(i => (byte)i).ToArray();

        await blockChain.Signer.Sign(validNonceTx);

        TransactionResult? result = null;

        try
        {
            result = transactionProcessor.Execute(validNonceTx, NullTxTracer.Instance);
        }
        catch
        {
            result = TransactionResult.Ok;
        }

        result.Value.Error.Should().NotBe(XdcTransactionResult.NonceTooHighError);
        result.Value.Error.Should().NotBe(XdcTransactionResult.NonceTooLowError);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Malformed_WrongBlockNumber_BlockTooHigh_SignTx_Fails_Validation(bool enableEip1559)
    {
        var epochLength = 10;
        var blockChain = await XdcTestBlockchain.Create(epochLength * 3, false);
        blockChain.ChangeReleaseSpec((spec) =>
        {
            spec.IsEip1559Enabled = enableEip1559;
            spec.EpochLength = epochLength;
        });

        var moqVm = new VirtualMachine(new BlockhashProvider(new BlockhashCache(blockChain.Container.Resolve<IHeaderFinder>(), NullLogManager.Instance), blockChain.MainWorldState, NullLogManager.Instance), blockChain.SpecProvider, NullLogManager.Instance);

        var transactionProcessor = new XdcTransactionProcessor(BlobBaseFeeCalculator.Instance, blockChain.SpecProvider, blockChain.MainWorldState, moqVm, NSubstitute.Substitute.For<ICodeInfoRepository>(), NullLogManager.Instance);


        XdcBlockHeader head = (XdcBlockHeader)blockChain.BlockTree.Head!.Header!;
        XdcReleaseSpec spec = (XdcReleaseSpec)blockChain.SpecProvider.GetXdcSpec(head);

        blockChain.MainWorldState.BeginScope(head);

        UInt256 tooHighBlockNumber = (UInt256)head.Number + 1;
        Transaction txTooHigh = SignTransactionManager.CreateTxSign(
            tooHighBlockNumber,
            head.Hash!,
            blockChain.TxPool.GetLatestPendingNonce(blockChain.Signer.Address),
            spec.BlockSignerContract,
            blockChain.Signer.Address);

        await blockChain.Signer.Sign(txTooHigh);

        var result = blockChain.TxPool.SubmitTx(txTooHigh, TxHandlingOptions.PersistentBroadcast);

        Assert.That(result, Is.EqualTo(AcceptTxResult.Invalid));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Malformed_WrongBlockNumber_BlockTooLow_SignTx_Fails_Validation(bool enableEip1559)
    {
        var epochLength = 10;
        var blockChain = await XdcTestBlockchain.Create(epochLength * 3, false);
        blockChain.ChangeReleaseSpec((spec) =>
        {
            spec.IsEip1559Enabled = enableEip1559;
            spec.EpochLength = epochLength;
        });

        var moqVm = new VirtualMachine(new BlockhashProvider(new BlockhashCache(blockChain.Container.Resolve<IHeaderFinder>(), NullLogManager.Instance), blockChain.MainWorldState, NullLogManager.Instance), blockChain.SpecProvider, NullLogManager.Instance);

        var transactionProcessor = new XdcTransactionProcessor(BlobBaseFeeCalculator.Instance, blockChain.SpecProvider, blockChain.MainWorldState, moqVm, NSubstitute.Substitute.For<ICodeInfoRepository>(), NullLogManager.Instance);


        XdcBlockHeader head = (XdcBlockHeader)blockChain.BlockTree.Head!.Header!;
        XdcReleaseSpec spec = (XdcReleaseSpec)blockChain.SpecProvider.GetXdcSpec(head);

        blockChain.MainWorldState.BeginScope(head);

        moqVm.SetBlockExecutionContext(new BlockExecutionContext(head, spec));

        long lowerBound = head.Number - (spec.EpochLength * 2);
        UInt256 tooLowBlockNumber = (UInt256)lowerBound;
        Transaction txTooLow = SignTransactionManager.CreateTxSign(
            tooLowBlockNumber,
            head.Hash!,
            blockChain.TxPool.GetLatestPendingNonce(blockChain.Signer.Address),
            spec.BlockSignerContract,
            blockChain.Signer.Address);

        await blockChain.Signer.Sign(txTooLow);

        var result = blockChain.TxPool.SubmitTx(txTooLow, TxHandlingOptions.PersistentBroadcast);

        Assert.That(result, Is.EqualTo(AcceptTxResult.Invalid));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Malformed_WrongBlockNumber_BlockWithinRange_SignTx_Fails_Validation(bool enableEip1559)
    {
        var epochLength = 10;
        var blockChain = await XdcTestBlockchain.Create(epochLength * 3, false);
        blockChain.ChangeReleaseSpec((spec) =>
        {
            spec.IsEip1559Enabled = enableEip1559;
            spec.EpochLength = epochLength;
        });

        XdcBlockHeader head = (XdcBlockHeader)blockChain.BlockTree.Head!.Header!;
        XdcReleaseSpec spec = (XdcReleaseSpec)blockChain.SpecProvider.GetXdcSpec(head);

        // Header.Number is current block; we must pass:
        //   blkNumber < header.Number
        //   blkNumber > header.Number - (EpochLength * 2)
        //
        // Pick something comfortably in the middle of that interval.
        long upper = head.Number - 1;
        long lower = head.Number - (spec.EpochLength * 2) + 1;
        long validBlockNumber = lower + (upper - lower) / 2;

        Transaction tx =
            SignTransactionManager.CreateTxSign(
                (UInt256)validBlockNumber,
                head.Hash!,
                blockChain.TxPool.GetLatestPendingNonce(blockChain.Signer.Address),
                spec.BlockSignerContract,
                blockChain.Signer.Address);

        await blockChain.Signer.Sign(tx);

        var result = blockChain.TxPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);

        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task SignTx_Increments_Nonce_And_Emits_Log_And_Consume_NoGas(bool enableEip1559)
    {
        var blockChain = await XdcTestBlockchain.Create(5, false);
        blockChain.ChangeReleaseSpec((spec) =>
        {
            spec.IsEip1559Enabled = enableEip1559;
        });

        var moqVm = new VirtualMachine(new BlockhashProvider(new BlockhashCache(blockChain.Container.Resolve<IHeaderFinder>(), NullLogManager.Instance), blockChain.MainWorldState, NullLogManager.Instance), blockChain.SpecProvider, NullLogManager.Instance);

        var transactionProcessor = new XdcTransactionProcessor(BlobBaseFeeCalculator.Instance, blockChain.SpecProvider, blockChain.MainWorldState, moqVm, NSubstitute.Substitute.For<ICodeInfoRepository>(), NullLogManager.Instance);


        Block head = (Block)blockChain.BlockTree.Head!;
        XdcReleaseSpec spec = (XdcReleaseSpec)blockChain.SpecProvider.GetXdcSpec((XdcBlockHeader)head.Header);

        blockChain.MainWorldState.BeginScope(head.Header);

        moqVm.SetBlockExecutionContext(new BlockExecutionContext(head.Header, spec));

        UInt256 initialNonce = blockChain.MainWorldState.GetNonce(blockChain.Signer.Address);
        UInt256 initialBalance = blockChain.MainWorldState.GetBalance(blockChain.Signer.Address);

        Transaction? tx = SignTransactionManager.CreateTxSign((UInt256)head.Number - 1, head.ParentHash!, initialNonce, spec.BlockSignerContract, blockChain.Signer.Address);

        await blockChain.Signer.Sign(tx);

        var receiptsTracer = new BlockReceiptsTracer();

        var initialCountOfReceipts = receiptsTracer.TxReceipts.Length;

        receiptsTracer.StartNewBlockTrace(head);
        receiptsTracer.StartNewTxTrace(tx);

        TransactionResult? result = transactionProcessor.Execute(tx, receiptsTracer);

        Assert.That(result.Value.EvmExceptionType, Is.EqualTo(EvmExceptionType.None), $"specialTx to {spec.BlockSignerContract} should succeed");


        receiptsTracer.EndTxTrace();
        receiptsTracer.EndBlockTrace();

        UInt256 finalNonce = blockChain.MainWorldState.GetNonce(blockChain.Signer.Address);
        UInt256 finalBalance = blockChain.MainWorldState.GetBalance(blockChain.Signer.Address);

        Assert.That(finalNonce, Is.EqualTo(initialNonce + 1));
        Assert.That(finalBalance, Is.EqualTo(initialBalance));

        var finalCountOfReceipts = receiptsTracer.TxReceipts.Length;

        Assert.That(finalCountOfReceipts, Is.EqualTo(initialCountOfReceipts + 1));

        var finalReceipt = receiptsTracer.TxReceipts[^1];

        Assert.That(finalReceipt?.Logs?.Length, Is.EqualTo(1));

        Assert.That(finalReceipt?.Logs?[0].Address, Is.EqualTo(spec.BlockSignerContract));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Valid_SpecialTx_NotSign_Call_EmptyTx_Handler(bool enableEip1559)
    {
        var blockChain = await XdcTestBlockchain.Create(5, false);
        blockChain.ChangeReleaseSpec((spec) =>
        {
            spec.IsEip1559Enabled = enableEip1559;

            spec.IsTIPXDCXMiner = true;

            spec.TradingStateAddressBinary = new Address("0x00000000000000000000000000000000b000091");
            spec.XDCXAddressBinary = new Address("0x00000000000000000000000000000000b000092");
            spec.XDCXLendingAddressBinary = new Address("0x00000000000000000000000000000000b000093");
            spec.XDCXLendingFinalizedTradeAddressBinary = new Address("0x00000000000000000000000000000000b000094");
        });

        var moqVm = new VirtualMachine(new BlockhashProvider(new BlockhashCache(blockChain.Container.Resolve<IHeaderFinder>(), NullLogManager.Instance), blockChain.MainWorldState, NullLogManager.Instance), blockChain.SpecProvider, NullLogManager.Instance);

        var transactionProcessor = new XdcTransactionProcessor(BlobBaseFeeCalculator.Instance, blockChain.SpecProvider, blockChain.MainWorldState, moqVm, NSubstitute.Substitute.For<ICodeInfoRepository>(), NullLogManager.Instance);


        Block head = (Block)blockChain.BlockTree.Head!;
        XdcReleaseSpec spec = (XdcReleaseSpec)blockChain.SpecProvider.GetXdcSpec((XdcBlockHeader)head.Header);

        blockChain.MainWorldState.BeginScope(head.Header);

        moqVm.SetBlockExecutionContext(new BlockExecutionContext(head.Header, spec));

        Address[] addresses = [
            spec.TradingStateAddressBinary,
            spec.XDCXLendingAddressBinary,
            spec.XDCXAddressBinary,
            spec.XDCXLendingFinalizedTradeAddressBinary,
            ];

        var receiptsTracer = new BlockReceiptsTracer();

        receiptsTracer.StartNewBlockTrace(head);
        foreach (var address in addresses)
        {
            UInt256 initialNonce = blockChain.MainWorldState.GetNonce(blockChain.Signer.Address);
            UInt256 initialBalance = blockChain.MainWorldState.GetBalance(blockChain.Signer.Address);

            Transaction? tx = Build.A.Transaction
                .WithType(TxType.Legacy)
                .WithSenderAddress(blockChain.Signer.Address)
                .WithTo(address).TestObject;

            await blockChain.Signer.Sign(tx);

            var initialCountOfReceipts = receiptsTracer.TxReceipts.Length;

            receiptsTracer.StartNewTxTrace(tx);

            TransactionResult? result = transactionProcessor.Execute(tx, receiptsTracer);

            Assert.That(result.Value.EvmExceptionType, Is.EqualTo(EvmExceptionType.None), $"specialTx to {address} should succeed");

            receiptsTracer.EndTxTrace();

            UInt256 finalNonce = blockChain.MainWorldState.GetNonce(blockChain.Signer.Address);
            UInt256 finalBalance = blockChain.MainWorldState.GetBalance(blockChain.Signer.Address);

            Assert.That(finalNonce, Is.EqualTo(initialNonce), $"specialTx to {address} does not increment nonce, initialNonce: {initialNonce}, finalNonce: {finalNonce}");

            Assert.That(initialBalance, Is.EqualTo(finalBalance), $"specialTx to {address} does not increment nonce, initialBalance: {initialNonce}, finalBalance: {finalNonce}");

            var finalCountOfReceipts = receiptsTracer.TxReceipts.Length;

            Assert.That(finalCountOfReceipts, Is.EqualTo(initialCountOfReceipts + 1));

            var finalReceipt = receiptsTracer.TxReceipts[^1];

            Assert.That(finalReceipt?.Logs?.Length, Is.EqualTo(1));

            Assert.That(finalReceipt?.Logs?[0].Address, Is.EqualTo(address));
        }
        receiptsTracer.EndBlockTrace();

        Assert.That(receiptsTracer.TxReceipts.Length, Is.EqualTo(addresses.Length));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SignTx_With_ZeroBalance_CanBeIncludedInBlock_And_ReceiptIsEmitted(bool enableEip1559)
    {
        var chain = await XdcTestBlockchain.Create();

        chain.ChangeReleaseSpec((spec) =>
        {
            spec.IsEip1559Enabled = enableEip1559;
        });

        var head = (XdcBlockHeader)chain.BlockTree.Head!.Header;
        IXdcReleaseSpec spec = chain.SpecProvider.GetXdcSpec(head, chain.XdcContext.CurrentRound);
        var epochLength = spec.EpochLength;

        // Add blocks up to epochLength (E) + 15 and create a signing tx that will be inserted in the next block
        await chain.AddBlocks(epochLength + 15 - 3);
        var header915 = chain.BlockTree.Head!.Header as XdcBlockHeader;
        Assert.That(header915, Is.Not.Null);
        PrivateKey signer915 = chain.Signer.Key!;
        Address owner = signer915.Address;

        // Ensure signer has 0 balance BEFORE the block that includes the SignTx is committed
        using (chain.MainWorldState.BeginScope(header915!))
        {
            chain.MainWorldState.CreateAccountIfNotExists(chain.Signer.Address, UInt256.Zero);
            chain.MainWorldState.Commit((IReleaseSpec)spec, NullStateTracer.Instance);
        }

        var signTxManager = chain.Container.Resolve<ISignTransactionManager>();
        await signTxManager.SubmitTransactionSign(head, spec);

        await chain.AddBlockMayHaveExtraTx();

        var block = (XdcBlockHeader)chain.BlockTree.Head!.Header;

        // Get receipts of the block after (i.e., the block that included the SignTx)
        var receipts = chain.ReceiptStorage.Get(block.Hash!);
        Assert.That(receipts, Is.Not.Null);
        Assert.That(receipts, Is.Not.Empty);

        receipts.Any(r => r.Recipient == spec.BlockSignerContract).Should().BeTrue();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RandomizeTx_IncrementNonce_And_Is_Treated_As_Free(bool enableEip1559)
    {
        var blockChain = await XdcTestBlockchain.Create(5, false);
        blockChain.ChangeReleaseSpec((spec) =>
        {
            spec.IsEip1559Enabled = enableEip1559;
        });

        var moqVm = new VirtualMachine(new BlockhashProvider(new BlockhashCache(blockChain.Container.Resolve<IHeaderFinder>(), NullLogManager.Instance), blockChain.MainWorldState, NullLogManager.Instance), blockChain.SpecProvider, NullLogManager.Instance);

        var transactionProcessor = blockChain.TxProcessor as XdcTransactionProcessor;

        Block head = (Block)blockChain.BlockTree.Head!;
        XdcReleaseSpec spec = (XdcReleaseSpec)blockChain.SpecProvider.GetXdcSpec((XdcBlockHeader)head.Header);

        // Ensure signer has 0 balance BEFORE the block that includes the SignTx is committed
        using (blockChain.MainWorldState.BeginScope(head.Header!))
        {
            byte[] dummyRuntimeCode = [0x60, 0x00, 0x60, 0x00, 0x01, 0x50, 0x00]; // PUSH1 PUSH1 ADD POP STOP
            blockChain.MainWorldState.CreateAccountIfNotExists(spec.RandomizeSMCBinary, UInt256.Zero);
            blockChain.MainWorldState.InsertCode(
                spec.RandomizeSMCBinary,
                Keccak.Compute(dummyRuntimeCode),
                dummyRuntimeCode,
                spec);
        }

        blockChain.MainWorldState.BeginScope(head.Header);
        moqVm.SetBlockExecutionContext(new BlockExecutionContext(head.Header, spec));

        UInt256 initialNonce = blockChain.MainWorldState.GetNonce(blockChain.Signer.Address);
        UInt256 initialBalance = blockChain.MainWorldState.GetBalance(blockChain.Signer.Address);

        var tx = Build.A.Transaction
            .WithType(TxType.Legacy)
            .WithSenderAddress(blockChain.Signer.Address)
            .WithTo(spec.RandomizeSMCBinary)
            .WithGasPrice(UInt256.Zero)
            .WithValue(UInt256.Zero)
            .TestObject;

        await blockChain.Signer.Sign(tx);

        var receiptsTracer = new BlockReceiptsTracer();
        receiptsTracer.StartNewBlockTrace(head);
        receiptsTracer.StartNewTxTrace(tx);

        TransactionResult result = transactionProcessor!.Execute(tx, receiptsTracer);

        receiptsTracer.EndTxTrace();
        receiptsTracer.EndBlockTrace();

        result.Error.Should().Be(TransactionResult.ErrorType.None);

        UInt256 finalNonce = blockChain.MainWorldState.GetNonce(blockChain.Signer.Address);
        UInt256 finalBalance = blockChain.MainWorldState.GetBalance(blockChain.Signer.Address);

        finalNonce.Should().Be(initialNonce + 1);
        finalBalance.Should().Be(initialBalance); // zero gas price => no balance change

        receiptsTracer.TxReceipts.Length.Should().NotBe(0);
        var receipt = receiptsTracer.TxReceipts[^1];
        receipt.GasUsed.Should().BeGreaterThan(0);
    }


    [TestCase(true)]
    [TestCase(false)]
    public async Task RandomizeTx_From_ZeroBalance_Account(bool enableEip1559)
    {
        var blockChain = await XdcTestBlockchain.Create(5, false);
        blockChain.ChangeReleaseSpec((spec) =>
        {
            spec.IsEip1559Enabled = enableEip1559;
        });

        var moqVm = new VirtualMachine(new BlockhashProvider(new BlockhashCache(blockChain.Container.Resolve<IHeaderFinder>(), NullLogManager.Instance), blockChain.MainWorldState, NullLogManager.Instance), blockChain.SpecProvider, NullLogManager.Instance);

        var transactionProcessor = blockChain.TxProcessor as XdcTransactionProcessor;

        Block head = (Block)blockChain.BlockTree.Head!;
        XdcReleaseSpec spec = (XdcReleaseSpec)blockChain.SpecProvider.GetXdcSpec((XdcBlockHeader)head.Header);

        moqVm.SetBlockExecutionContext(new BlockExecutionContext(head.Header, spec));

        using (blockChain.MainWorldState.BeginScope(head.Header!))
        {
            blockChain.MainWorldState.CreateAccountIfNotExists(blockChain.Signer.Address, UInt256.Zero);
            blockChain.MainWorldState.Commit((IReleaseSpec)spec, NullStateTracer.Instance);

            byte[] dummyRuntimeCode = [0x60, 0x00, 0x60, 0x00, 0x01, 0x50, 0x00]; // PUSH1 PUSH1 ADD POP STOP
            blockChain.MainWorldState.CreateAccountIfNotExists(spec.RandomizeSMCBinary, UInt256.Zero);
            blockChain.MainWorldState.InsertCode(
                spec.RandomizeSMCBinary,
                Keccak.Compute(dummyRuntimeCode),
                dummyRuntimeCode,
                spec);
        }

        blockChain.MainWorldState.BeginScope(head.Header);

        UInt256 initialNonce = blockChain.MainWorldState.GetNonce(blockChain.Signer.Address);
        UInt256 initialBalance = blockChain.MainWorldState.GetBalance(blockChain.Signer.Address);

        var tx = Build.A.Transaction
            .WithType(TxType.Legacy)
            .WithSenderAddress(blockChain.Signer.Address)
            .WithTo(spec.RandomizeSMCBinary)
            .WithGasPrice(UInt256.Zero)
            .WithValue(UInt256.Zero)
            .TestObject;

        await blockChain.Signer.Sign(tx);

        var receiptsTracer = new BlockReceiptsTracer();
        receiptsTracer.StartNewBlockTrace(head);
        receiptsTracer.StartNewTxTrace(tx);

        TransactionResult result = transactionProcessor!.Execute(tx, receiptsTracer);

        receiptsTracer.EndTxTrace();
        receiptsTracer.EndBlockTrace();

        result.Error.Should().Be(TransactionResult.ErrorType.None);

        UInt256 finalNonce = blockChain.MainWorldState.GetNonce(blockChain.Signer.Address);
        UInt256 finalBalance = blockChain.MainWorldState.GetBalance(blockChain.Signer.Address);

        finalNonce.Should().Be(initialNonce + 1);
        finalBalance.Should().Be(initialBalance); // zero gas price => no balance change

        receiptsTracer.TxReceipts.Length.Should().NotBe(0);
        var receipt = receiptsTracer.TxReceipts[^1];
        receipt.GasUsed.Should().BeGreaterThan(0);
    }
}
