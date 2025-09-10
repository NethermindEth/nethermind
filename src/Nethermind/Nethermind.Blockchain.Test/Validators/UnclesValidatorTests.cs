// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Validators;

public class UnclesValidatorTests
{
    private Block _grandgrandparent;
    private Block _grandparent;
    private Block _parent;
    private Block _block;
    private IBlockTree _blockTree;
    private IHeaderValidator _headerValidator = null!;

    private Block _duplicateUncle;

    [SetUp]
    public void Setup()
    {
        _blockTree = Build.A.BlockTree().OfChainLength(1).TestObject;
        _grandgrandparent = _blockTree.FindBlock(0, BlockTreeLookupOptions.None)!;
        _grandparent = Build.A.Block.WithParent(_grandgrandparent).TestObject;
        _duplicateUncle = Build.A.Block.WithParent(_grandgrandparent).TestObject;
        _parent = Build.A.Block.WithParent(_grandparent).WithUncles(_duplicateUncle).TestObject;
        _block = Build.A.Block.WithParent(_parent).TestObject;

        _blockTree.SuggestHeader(_grandparent.Header);
        _blockTree.SuggestHeader(_parent.Header);
        _blockTree.SuggestHeader(_block.Header);

        _headerValidator = Substitute.For<IHeaderValidator>();
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void When_more_than_two_uncles_returns_false()
    {
        BlockHeader[] uncles = GetValidUncles(3);
        UnclesValidator unclesValidator = new(_blockTree, _headerValidator, LimboLogs.Instance);
        Assert.That(unclesValidator.Validate(Build.A.BlockHeader.TestObject, uncles), Is.False);
        _headerValidator.DidNotReceive().Validate(Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<bool>());
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void When_uncle_is_self_returns_false()
    {
        BlockHeader[] uncles = new BlockHeader[1];
        uncles[0] = _block.Header;
        SetupHeaderValidator(uncles);

        UnclesValidator unclesValidator = new(_blockTree, _headerValidator, LimboLogs.Instance);
        Assert.That(unclesValidator.Validate(_block.Header, uncles), Is.False);
        AssertHeaderValidatorReceivedProperCalls(uncles);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void When_uncle_is_brother_returns_false()
    {
        BlockHeader[] uncles = [Build.A.BlockHeader.TestObject];
        uncles[0].ParentHash = _parent.Hash;
        uncles[0].Number = _block.Number;
        SetupHeaderValidator(uncles);

        UnclesValidator unclesValidator = new(_blockTree, _headerValidator, LimboLogs.Instance);
        Assert.That(unclesValidator.Validate(_block.Header, uncles), Is.False);
        AssertHeaderValidatorReceivedProperCalls(uncles);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void When_uncle_is_parent_returns_false()
    {
        BlockHeader[] uncles = [_parent.Header];
        SetupHeaderValidator(uncles);
        UnclesValidator unclesValidator = new(_blockTree, _headerValidator, LimboLogs.Instance);
        Assert.That(unclesValidator.Validate(_block.Header, uncles), Is.False);
        AssertHeaderValidatorReceivedProperCalls(uncles);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void When_uncle_was_already_included_return_false()
    {
        BlockHeader[] uncles = { _duplicateUncle.Header };
        SetupHeaderValidator(uncles);
        UnclesValidator unclesValidator = new(_blockTree, _headerValidator, LimboLogs.Instance);
        Assert.That(unclesValidator.Validate(_block.Header, uncles), Is.False);
        AssertHeaderValidatorReceivedProperCalls(uncles);
    }

    private BlockHeader[] GetValidUncles(int count)
    {
        BlockHeader[] uncles = new BlockHeader[count];
        for (int i = 0; i < count; i++)
        {
            uncles[i] = Build.A.BlockHeader.WithParent(_grandparent.Header).TestObject;
        }

        return uncles;
    }

    private void SetupHeaderValidator(BlockHeader[] uncles)
    {
        foreach (BlockHeader uncle in uncles)
        {
            _headerValidator.Validate(uncle,
                    _blockTree.FindParentHeader(uncle, BlockTreeLookupOptions.TotalDifficultyNotNeeded), true)
                .Returns(true);
        }
    }

    private void AssertHeaderValidatorReceivedProperCalls(BlockHeader[] uncles)
    {
        foreach (BlockHeader uncle in uncles)
        {
            _headerValidator.Received(1).Validate(uncle,
                _blockTree.FindParentHeader(uncle, BlockTreeLookupOptions.TotalDifficultyNotNeeded), true);
        }

        Assert.That(_headerValidator.ReceivedCalls().Count, Is.EqualTo(uncles.Length)); // No other calls happened
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void When_all_is_fine_returns_true()
    {
        BlockHeader[] uncles = GetValidUncles(1);
        SetupHeaderValidator(uncles);

        UnclesValidator unclesValidator = new(_blockTree, _headerValidator, LimboLogs.Instance);
        Assert.That(unclesValidator.Validate(_block.Header, uncles));
        AssertHeaderValidatorReceivedProperCalls(uncles);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Grandpas_brother_is_fine()
    {
        BlockHeader[] uncles = GetValidUncles(1);
        uncles[0].Number = _grandparent.Number;
        uncles[0].ParentHash = _grandgrandparent.Hash;
        SetupHeaderValidator(uncles);

        UnclesValidator unclesValidator = new(_blockTree, _headerValidator, LimboLogs.Instance);
        Assert.That(unclesValidator.Validate(_block.Header, uncles), Is.True);
        AssertHeaderValidatorReceivedProperCalls(uncles);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Same_uncle_twice_returns_false()
    {
        BlockHeader[] uncles = GetValidUncles(1).Union(GetValidUncles(1)).ToArray();

        UnclesValidator unclesValidator = new(_blockTree, _headerValidator, LimboLogs.Instance);
        Assert.That(unclesValidator.Validate(_block.Header, uncles), Is.False);
        _headerValidator.DidNotReceive().Validate(Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<bool>());
    }

    [Test, MaxTime(Timeout.MaxTestTime)] // because we decided to store the head block at 0x00..., eh
    public void Uncles_near_genesis_with_00_address_used()
    {
        Block falseUncle = Build.A.Block.WithParent(Build.A.Block.WithDifficulty(123).TestObject).TestObject;
        Block toValidate = Build.A.Block.WithParent(_parent).WithUncles(falseUncle).TestObject;
        SetupHeaderValidator(toValidate.Uncles);
        UnclesValidator unclesValidator = new(_blockTree, _headerValidator, LimboLogs.Instance);
        Assert.That(unclesValidator.Validate(toValidate.Header, toValidate.Uncles), Is.False);
        AssertHeaderValidatorReceivedProperCalls(toValidate.Uncles);
    }
}
