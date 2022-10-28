//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Linq;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Validators
{
    [TestFixture]
    public class UnclesValidatorTests
    {
        private readonly Block _grandgrandparent;
        private readonly Block _grandparent;
        private readonly Block _parent;
        private readonly Block _block;
        private readonly IBlockTree _blockTree;
        private IHeaderValidator _headerValidator;

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
            _grandgrandparent = _blockTree.FindBlock(0, BlockTreeLookupOptions.None);
            _grandparent = Build.A.Block.WithParent(_grandgrandparent).TestObject;
            _duplicateUncle = Build.A.Block.WithParent(_grandgrandparent).TestObject;
            _parent = Build.A.Block.WithParent(_grandparent).WithUncles(_duplicateUncle).TestObject;
            _block = Build.A.Block.WithParent(_parent).TestObject;

            _blockTree.SuggestHeader(_grandparent.Header);
            _blockTree.SuggestHeader(_parent.Header);
            _blockTree.SuggestHeader(_block.Header);
        }

        [Test]
        public void When_more_than_two_uncles_returns_false()
        {
            BlockHeader[] uncles = GetValidUncles(3);

            UnclesValidator unclesValidator = new(_blockTree, _headerValidator, LimboLogs.Instance);
            Assert.False(unclesValidator.Validate(Build.A.BlockHeader.TestObject, uncles));
        }

        [Test]
        public void When_uncle_is_self_returns_false()
        {
            BlockHeader[] uncles = new BlockHeader[1];
            uncles[0] = _block.Header;

            UnclesValidator unclesValidator = new(_blockTree, _headerValidator, LimboLogs.Instance);
            Assert.False(unclesValidator.Validate(_block.Header, uncles));
        }

        [Test]
        public void When_uncle_is_brother_returns_false()
        {
            BlockHeader[] uncles = new BlockHeader[1];
            uncles[0] = Build.A.BlockHeader.TestObject;
            uncles[0].ParentHash = _parent.Hash;
            uncles[0].Number = _block.Number;

            UnclesValidator unclesValidator = new(_blockTree, _headerValidator, LimboLogs.Instance);
            Assert.False(unclesValidator.Validate(_block.Header, uncles));
        }

        [Test]
        public void When_uncle_is_parent_returns_false()
        {
            BlockHeader[] uncles = new BlockHeader[1];
            uncles[0] = _parent.Header;

            UnclesValidator unclesValidator = new(_blockTree, _headerValidator, LimboLogs.Instance);
            Assert.False(unclesValidator.Validate(_block.Header, uncles));
        }

        [Test]
        public void When_uncle_was_already_included_return_false()
        {
            UnclesValidator unclesValidator = new(_blockTree, _headerValidator, LimboLogs.Instance);
            Assert.False(unclesValidator.Validate(_block.Header, new[] { _duplicateUncle.Header }));
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

        [Test]
        public void When_all_is_fine_returns_true()
        {
            BlockHeader[] uncles = GetValidUncles(1);

            UnclesValidator unclesValidator = new(_blockTree, _headerValidator, LimboLogs.Instance);
            Assert.True(unclesValidator.Validate(_block.Header, uncles));
        }

        [Test]
        public void Grandpas_brother_is_fine()
        {
            BlockHeader[] uncles = GetValidUncles(1);
            uncles[0].Number = _grandparent.Number;
            uncles[0].ParentHash = _grandgrandparent.Hash;

            UnclesValidator unclesValidator = new(_blockTree, _headerValidator, LimboLogs.Instance);
            Assert.True(unclesValidator.Validate(_block.Header, uncles));
        }

        [Test]
        public void Same_uncle_twice_returns_false()
        {
            BlockHeader[] uncles = GetValidUncles(1).Union(GetValidUncles(1)).ToArray();

            UnclesValidator unclesValidator = new(_blockTree, _headerValidator, LimboLogs.Instance);
            Assert.False(unclesValidator.Validate(_block.Header, uncles));
        }

        [Test] // because we decided to store the head block at 0x00..., eh
        public void Uncles_near_genesis_with_00_address_used()
        {
            Block falseUncle = Build.A.Block.WithParent(Build.A.Block.WithDifficulty(123).TestObject).TestObject;
            Block toValidate = Build.A.Block.WithParent(_parent).WithUncles(falseUncle).TestObject;
            UnclesValidator unclesValidator = new(_blockTree, _headerValidator, LimboLogs.Instance);
            Assert.False(unclesValidator.Validate(toValidate.Header, toValidate.Uncles));
        }
    }
}
