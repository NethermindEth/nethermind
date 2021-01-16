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
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Validators
{
    [TestFixture]
    public class OmmersValidatorTests
    {
        private readonly Block _grandgrandparent;
        private readonly Block _grandparent;
        private readonly Block _parent;
        private readonly Block _block;
        private readonly IBlockTree _blockTree;
        private IHeaderValidator _headerValidator;

        private readonly Block _duplicateOmmer;

        [SetUp]
        public void Setup()
        {
            _headerValidator = Substitute.For<IHeaderValidator>();
            _headerValidator.Validate(Arg.Any<BlockHeader>(), true).Returns(true);
        }

        public OmmersValidatorTests()
        {
            _blockTree = Build.A.BlockTree().OfChainLength(1).TestObject;
            _grandgrandparent = _blockTree.FindBlock(0, BlockTreeLookupOptions.None);
            _grandparent = Build.A.Block.WithParent(_grandgrandparent).TestObject;
            _duplicateOmmer = Build.A.Block.WithParent(_grandgrandparent).TestObject;
            _parent = Build.A.Block.WithParent(_grandparent).WithOmmers(_duplicateOmmer).TestObject;
            _block = Build.A.Block.WithParent(_parent).TestObject;

            _blockTree.SuggestHeader(_grandparent.Header);
            _blockTree.SuggestHeader(_parent.Header);
            _blockTree.SuggestHeader(_block.Header);
        }

        [Test]
        public void When_more_than_two_ommers_returns_false()
        {
            BlockHeader[] ommers = GetValidOmmers(3);

            OmmersValidator ommersValidator = new OmmersValidator(_blockTree, _headerValidator, LimboLogs.Instance);
            Assert.False(ommersValidator.Validate(Build.A.BlockHeader.TestObject, ommers));
        }

        [Test]
        public void When_ommer_is_self_returns_false()
        {
            BlockHeader[] ommers = new BlockHeader[1];
            ommers[0] = _block.Header;

            OmmersValidator ommersValidator = new OmmersValidator(_blockTree, _headerValidator, LimboLogs.Instance);
            Assert.False(ommersValidator.Validate(_block.Header, ommers));
        }

        [Test]
        public void When_ommer_is_brother_returns_false()
        {
            BlockHeader[] ommers = new BlockHeader[1];
            ommers[0] = Build.A.BlockHeader.TestObject;
            ommers[0].ParentHash = _parent.Hash;
            ommers[0].Number = _block.Number;

            OmmersValidator ommersValidator = new OmmersValidator(_blockTree, _headerValidator, LimboLogs.Instance);
            Assert.False(ommersValidator.Validate(_block.Header, ommers));
        }

        [Test]
        public void When_ommer_is_parent_returns_false()
        {
            BlockHeader[] ommers = new BlockHeader[1];
            ommers[0] = _parent.Header;

            OmmersValidator ommersValidator = new OmmersValidator(_blockTree, _headerValidator, LimboLogs.Instance);
            Assert.False(ommersValidator.Validate(_block.Header, ommers));
        }

        [Test]
        public void When_ommer_was_already_included_return_false()
        {
            OmmersValidator ommersValidator = new OmmersValidator(_blockTree, _headerValidator, LimboLogs.Instance);
            Assert.False(ommersValidator.Validate(_block.Header, new[] {_duplicateOmmer.Header}));
        }

        private BlockHeader[] GetValidOmmers(int count)
        {
            BlockHeader[] ommers = new BlockHeader[count];
            for (int i = 0; i < count; i++)
            {
                ommers[i] = Build.A.BlockHeader.WithParent(_grandparent.Header).TestObject;
            }

            return ommers;
        }

        [Test]
        public void When_all_is_fine_returns_true()
        {
            BlockHeader[] ommers = GetValidOmmers(1);

            OmmersValidator ommersValidator = new OmmersValidator(_blockTree, _headerValidator, LimboLogs.Instance);
            Assert.True(ommersValidator.Validate(_block.Header, ommers));
        }

        [Test]
        public void Grandpas_brother_is_fine()
        {
            BlockHeader[] ommers = GetValidOmmers(1);
            ommers[0].Number = _grandparent.Number;
            ommers[0].ParentHash = _grandgrandparent.Hash;

            OmmersValidator ommersValidator = new OmmersValidator(_blockTree, _headerValidator, LimboLogs.Instance);
            Assert.True(ommersValidator.Validate(_block.Header, ommers));
        }

        [Test]
        public void Same_ommer_twice_returns_false()
        {
            BlockHeader[] ommers = GetValidOmmers(1).Union(GetValidOmmers(1)).ToArray();

            OmmersValidator ommersValidator = new OmmersValidator(_blockTree, _headerValidator, LimboLogs.Instance);
            Assert.False(ommersValidator.Validate(_block.Header, ommers));
        }

        [Test] // because we decided to store the head block at 0x00..., eh
        public void Ommers_near_genesis_with_00_address_used()
        {
            Block falseOmmer = Build.A.Block.WithParent(Build.A.Block.WithDifficulty(123).TestObject).TestObject;
            Block toValidate = Build.A.Block.WithParent(_parent).WithOmmers(falseOmmer).TestObject;
            OmmersValidator ommersValidator = new OmmersValidator(_blockTree, _headerValidator, LimboLogs.Instance);
            Assert.False(ommersValidator.Validate(toValidate.Header, toValidate.Ommers));
        }
    }
}
