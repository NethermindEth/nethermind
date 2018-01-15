/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Linq;
using Nevermind.Blockchain;
using Nevermind.Blockchain.Validators;
using Nevermind.Core.Crypto;
using NSubstitute;
using NUnit.Framework;

namespace Nevermind.Core.Test
{
    [TestFixture]
    public class OmmersValidatorTests
    {
        private readonly BlockHeader _grandgrandparent;
        private readonly BlockHeader _grandparent;
        private readonly BlockHeader _parent;
        private readonly BlockHeader _header;
        private IBlockStore _blockStore;
        private IBlockHeaderValidator _blockHeaderValidator;

        private BlockHeader _duplicateOmmer;

        [SetUp]
        public void Setup()
        {
            _duplicateOmmer = new BlockHeader();
            _duplicateOmmer.Hash = Keccak.Compute("duplicate_ommer");

            _blockStore = Substitute.For<IBlockStore>();
            _blockStore.FindBlock(_grandgrandparent.Hash, false).Returns(new Block(_grandgrandparent, _duplicateOmmer));
            _blockStore.FindBlock(_grandparent.Hash, false).Returns(new Block(_grandparent));
            _blockStore.FindBlock(_parent.Hash, false).Returns(new Block(_parent));
            _blockStore.FindBlock(_header.Hash, false).Returns(new Block(_header));

            _blockHeaderValidator = Substitute.For<IBlockHeaderValidator>();
            _blockHeaderValidator.Validate(Arg.Any<BlockHeader>()).Returns(true);
        }

        public OmmersValidatorTests()
        {
            _grandgrandparent = new BlockHeader();
            _grandgrandparent.Number = 1;
            _grandgrandparent.Hash = Keccak.Compute("grandgrandpa");

            _grandparent = new BlockHeader();
            _grandparent.Number = _grandgrandparent.Number + 1;
            _grandparent.Hash = Keccak.Compute("grandpa");
            _grandparent.ParentHash = _grandgrandparent.Hash;

            _parent = new BlockHeader();
            _parent.Number = _grandparent.Number + 1;
            _parent.Hash = Keccak.Compute("parent");
            _parent.ParentHash = _grandparent.Hash;

            _header = new BlockHeader();
            _header.Number = _parent.Number + 1;
            _header.Hash = Keccak.Compute("header");
            _header.ParentHash = _parent.Hash;
        }

        [Test]
        public void When_more_than_two_ommers_returns_false()
        {
            IBlockHeaderValidator blockHeaderValidator = Substitute.For<IBlockHeaderValidator>();
            blockHeaderValidator.Validate(Arg.Any<BlockHeader>()).Returns(true);

            BlockHeader[] ommers = GetValidOmmers(3);

            OmmersValidator ommersValidator = new OmmersValidator(_blockStore, blockHeaderValidator);
            Assert.False(ommersValidator.Validate(new BlockHeader(), ommers));
        }

        [Test]
        public void When_ommer_is_self_returns_false()
        {
            IBlockHeaderValidator blockHeaderValidator = Substitute.For<IBlockHeaderValidator>();
            blockHeaderValidator.Validate(Arg.Any<BlockHeader>()).Returns(true);

            BlockHeader[] ommers = new BlockHeader[1];
            ommers[0] = _header;

            OmmersValidator ommersValidator = new OmmersValidator(_blockStore, blockHeaderValidator);
            Assert.False(ommersValidator.Validate(_header, ommers));
        }

        [Test]
        public void When_ommer_is_brother_returns_false()
        {
            BlockHeader[] ommers = new BlockHeader[1];
            ommers[0] = new BlockHeader();
            ommers[0].ParentHash = _parent.Hash;
            ommers[0].Number = _header.Number;

            OmmersValidator ommersValidator = new OmmersValidator(_blockStore, _blockHeaderValidator);
            Assert.False(ommersValidator.Validate(_header, ommers));
        }

        [Test]
        public void When_ommer_is_father_returns_false()
        {
            BlockHeader[] ommers = new BlockHeader[1];
            ommers[0] = _parent;

            OmmersValidator ommersValidator = new OmmersValidator(_blockStore, _blockHeaderValidator);
            Assert.False(ommersValidator.Validate(_header, ommers));
        }

        [Test]
        public void When_ommer_was_already_included_return_false()
        {
            OmmersValidator ommersValidator = new OmmersValidator(_blockStore, _blockHeaderValidator);
            Assert.False(ommersValidator.Validate(_header, new [] { _duplicateOmmer }));
        }

        private BlockHeader[] GetValidOmmers(int count)
        {
            // TODO: how these could be valid if they are obviously not valid?
            BlockHeader[] ommers = new BlockHeader[count];
            for (int i = 0; i < count; i++)
            {
                ommers[0] = new BlockHeader();
                ommers[0].Hash = Keccak.Compute("ommer" + i);
                ommers[0].ParentHash = _grandparent.Hash;
                ommers[0].Number = _parent.Number;
            }

            return ommers;
        }

        [Test]
        public void When_all_is_fine_returns_true()
        {
            BlockHeader[] ommers = GetValidOmmers(1);

            OmmersValidator ommersValidator = new OmmersValidator(_blockStore, _blockHeaderValidator);
            Assert.True(ommersValidator.Validate(_header, ommers));
        }

        [Test]
        public void Grandpas_brother_is_fine()
        {
            BlockHeader[] ommers = GetValidOmmers(1);
            ommers[0].Number = _grandparent.Number;
            ommers[0].ParentHash = _grandgrandparent.Hash;

            OmmersValidator ommersValidator = new OmmersValidator(_blockStore, _blockHeaderValidator);
            Assert.True(ommersValidator.Validate(_header, ommers));
        }

        [Test]
        public void Same_ommer_twice_returns_false()
        {
            BlockHeader[] ommers = GetValidOmmers(1).Union(GetValidOmmers(1)).ToArray();

            OmmersValidator ommersValidator = new OmmersValidator(_blockStore, _blockHeaderValidator);
            Assert.False(ommersValidator.Validate(_header, ommers));
        }
    }
}