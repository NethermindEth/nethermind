// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using Nethermind.Trie.Pruning;
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
        _specProvider = new CustomSpecProvider(((ForkActivation)0, Fork7805.Instance));

        // MemDb stateDb = new();
        // TrieStore trieStore = TestTrieStoreFactory.Build(stateDb, LimboLogs.Instance);
        // _stateProvider = new WorldState(trieStore, new MemDb(), LimboLogs.Instance);
        WorldStateManager worldStateManager = TestWorldStateFactory.CreateForTest();
        _stateProvider = worldStateManager.GlobalWorldState;
        _stateProvider.CreateAccount(TestItem.AddressA, 10.Ether());
        _stateProvider.Commit(_specProvider.GenesisSpec);
        _stateProvider.CommitTree(0);

        _inclusionListValidator = new InclusionListValidator(
            _specProvider,
            _stateProvider);

        _validTx = Build.A.Transaction
            .WithGasLimit(100_000)
            .WithGasPrice(10.GWei())
            .WithNonce(0)
            .WithValue(1.Ether())
            .WithTo(TestItem.AddressA)
            .SignedAndResolved(TestItem.PrivateKeyA)
            .TestObject;
    }

    [Test]
    public void When_block_full_then_accept()
    {
        Block block = Build.A.Block
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
        Block block = Build.A.Block
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
        Block block = Build.A.Block
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
        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithGasUsed(1_000_000)
            .TestObject;

        bool isValid = _inclusionListValidator.ValidateInclusionList(block, _ => false);
        Assert.That(isValid, Is.False);
    }
}
