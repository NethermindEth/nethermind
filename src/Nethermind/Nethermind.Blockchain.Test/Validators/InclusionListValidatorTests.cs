// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Validators;

public class InclusionListValidatorTests
{
    private ITransactionProcessor _transactionProcessor;
    private ISpecProvider _specProvider;
    private BlockValidator _blockValidator;
    private Transaction _validTx;
    private byte[] _validTxBytes;

    [SetUp]
    public void Setup()
    {
        _transactionProcessor = Substitute.For<ITransactionProcessor>();
        _specProvider = new CustomSpecProvider(((ForkActivation)0, Osaka.Instance));
        _blockValidator = new BlockValidator(
            Always.Valid,
            Always.Valid,
            Always.Valid,
            _specProvider,
            _transactionProcessor,
            LimboLogs.Instance);

        _validTx = Build.A.Transaction
            .WithGasLimit(100_000)
            .WithGasPrice(10.GWei())
            .WithNonce(1)
            .WithValue(100.Ether())
            .WithTo(TestItem.AddressA)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;

        _validTxBytes = Rlp.Encode(_validTx).Bytes;
    }

    [Test]
    public void When_block_full_then_accept()
    {
        var block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(30_000_000)
            .WithInclusionListTransactions([_validTxBytes])
            .TestObject;

        bool isValid = _blockValidator.ValidateInclusionList(block, out string? error);
        Assert.That(isValid, Is.True);
        Assert.That(error, Is.Null);
    }

    [Test]
    public void When_all_inclusion_list_txs_included_then_accept()
    {
        var block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(1_000_000)
            .WithTransactions(_validTx)
            .WithInclusionListTransactions([_validTxBytes])
            .TestObject;

        bool isValid = _blockValidator.ValidateInclusionList(block, out string? error);
        Assert.Multiple(() =>
        {
            Assert.That(isValid, Is.True);
            Assert.That(error, Is.Null);
        });
    }

    [Test]
    public void When_valid_tx_excluded_then_reject()
    {
        _transactionProcessor.BuildUp(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>())
            .Returns(TransactionResult.Ok);

        var block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(1_000_000)
            .WithInclusionListTransactions([_validTxBytes])
            .TestObject;

        bool isValid = _blockValidator.ValidateInclusionList(block, out string? error);
        Assert.Multiple(() =>
        {
            Assert.That(isValid, Is.False);
            Assert.That(error, Is.EqualTo("Block excludes valid inclusion list transaction"));
        });
    }

    [Test]
    public void When_no_inclusion_list_then_reject()
    {
        var block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(1_000_000)
            .TestObject;

        bool isValid = _blockValidator.ValidateInclusionList(block, out string? error);
        Assert.Multiple(() =>
        {
            Assert.That(isValid, Is.False);
            Assert.That(error, Is.EqualTo("Block did not have inclusion list"));
        });
    }
}
