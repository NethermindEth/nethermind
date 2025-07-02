// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Validators;

public class UnclesValidatorTests
{
    private readonly Block _grandgrandparent;
    private readonly Block _grandparent;
    private readonly Block _parent;
    private readonly Block _block;
    private readonly IBlockTree _blockTree;
    private IHeaderValidator _headerValidator = null!;

    private readonly Block _duplicateUncle;

    [SetUp]
    public void Setup()
    {
        _headerValidator = Substitute.For<IHeaderValidator>();
        _headerValidator.Validate(Arg.Any<BlockHeader>(), true).Returns(true);
    }

    public UnclesValidatorTests()
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
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void When_more_than_two_uncles_returns_false()
    {
        BlockHeader[] uncles = GetValidUncles(3);

        UnclesValidator unclesValidator = new(_blockTree, _headerValidator, LimboLogs.Instance);
        Assert.That(unclesValidator.Validate(Build.A.BlockHeader.TestObject, uncles), Is.False);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void When_uncle_is_self_returns_false()
    {
        BlockHeader[] uncles = new BlockHeader[1];
        uncles[0] = _block.Header;

        UnclesValidator unclesValidator = new(_blockTree, _headerValidator, LimboLogs.Instance);
        Assert.That(unclesValidator.Validate(_block.Header, uncles), Is.False);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void When_uncle_is_brother_returns_false()
    {
        BlockHeader[] uncles = [Build.A.BlockHeader.TestObject];
        uncles[0].ParentHash = _parent.Hash;
        uncles[0].Number = _block.Number;

        UnclesValidator unclesValidator = new(_blockTree, _headerValidator, LimboLogs.Instance);
        Assert.That(unclesValidator.Validate(_block.Header, uncles), Is.False);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void When_uncle_is_parent_returns_false()
    {
        BlockHeader[] uncles = [_parent.Header];
        UnclesValidator unclesValidator = new(_blockTree, _headerValidator, LimboLogs.Instance);
        Assert.That(unclesValidator.Validate(_block.Header, uncles), Is.False);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void When_uncle_was_already_included_return_false()
    {
        UnclesValidator unclesValidator = new(_blockTree, _headerValidator, LimboLogs.Instance);
        Assert.That(unclesValidator.Validate(_block.Header, [_duplicateUncle.Header]), Is.False);
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

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void When_all_is_fine_returns_true()
    {
        BlockHeader[] uncles = GetValidUncles(1);

        UnclesValidator unclesValidator = new(_blockTree, _headerValidator, LimboLogs.Instance);
        Assert.That(unclesValidator.Validate(_block.Header, uncles));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Grandpas_brother_is_fine()
    {
        BlockHeader[] uncles = GetValidUncles(1);
        uncles[0].Number = _grandparent.Number;
        uncles[0].ParentHash = _grandgrandparent.Hash;

        UnclesValidator unclesValidator = new(_blockTree, _headerValidator, LimboLogs.Instance);
        Assert.That(unclesValidator.Validate(_block.Header, uncles), Is.True);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Same_uncle_twice_returns_false()
    {
        BlockHeader[] uncles = GetValidUncles(1).Union(GetValidUncles(1)).ToArray();

        UnclesValidator unclesValidator = new(_blockTree, _headerValidator, LimboLogs.Instance);
        Assert.That(unclesValidator.Validate(_block.Header, uncles), Is.False);
    }

    [Test, MaxTime(Timeout.MaxTestTime)] // because we decided to store the head block at 0x00..., eh
    public void Uncles_near_genesis_with_00_address_used()
    {
        Block falseUncle = Build.A.Block.WithParent(Build.A.Block.WithDifficulty(123).TestObject).TestObject;
        Block toValidate = Build.A.Block.WithParent(_parent).WithUncles(falseUncle).TestObject;
        UnclesValidator unclesValidator = new(_blockTree, _headerValidator, LimboLogs.Instance);
        Assert.That(unclesValidator.Validate(toValidate.Header, toValidate.Uncles), Is.False);
    }
}
