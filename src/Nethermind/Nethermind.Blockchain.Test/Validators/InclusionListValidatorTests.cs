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
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Validators;

public class InclusionListValidatorTests
{
    private IWorldState _stateProvider;
    private ISpecProvider _specProvider;
    private InclusionListValidator _inclusionListValidator;
    private Transaction _validTx;

    [SetUp]
    public void Setup()
    {
        _stateProvider = Substitute.For<IWorldState>();
        _specProvider = new CustomSpecProvider(((ForkActivation)0, Fork7805.Instance));
        _inclusionListValidator = new InclusionListValidator(
            _specProvider,
            _stateProvider);

        _validTx = Build.A.Transaction
            .WithGasLimit(100_000)
            .WithGasPrice(10.GWei())
            .WithNonce(1)
            .WithValue(100.Ether())
            .WithTo(TestItem.AddressA)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
    }

    [Test]
    public void When_block_full_then_accept()
    {
        var block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(30_000_000)
            .WithInclusionListTransactions([_validTx])
            .TestObject;

        bool isValid = _inclusionListValidator.ValidateInclusionList(block, _ => false);
        Assert.That(isValid, Is.True);
    }

    [Test]
    public void When_all_inclusion_list_txs_included_then_accept()
    {
        var block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(1_000_000)
            .WithTransactions(_validTx)
            .WithInclusionListTransactions([_validTx])
            .TestObject;

        bool isValid = _inclusionListValidator.ValidateInclusionList(block, tx => tx == _validTx);
        Assert.That(isValid, Is.True);
    }

    [Test]
    public void When_valid_tx_excluded_then_reject()
    {
        // _transactionProcessor.BuildUp(Arg.Any<Transaction>(), Arg.Any<BlockExecutionContext>(), Arg.Any<ITxTracer>())
        //     .Returns(TransactionResult.Ok);
		// todo: fake world state

        var block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(1_000_000)
            .WithInclusionListTransactions([_validTx])
            .TestObject;

        bool isValid = _inclusionListValidator.ValidateInclusionList(block, _ => false);
        Assert.That(isValid, Is.False);
    }

    [Test]
    public void When_no_inclusion_list_then_reject()
    {
        var block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(1_000_000)
            .TestObject;

        bool isValid = _inclusionListValidator.ValidateInclusionList(block, _ => false);
        Assert.That(isValid, Is.False);
    }
}
