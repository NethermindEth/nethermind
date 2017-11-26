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
        private IBlockchainStore _blockchainStore;
        private IBlockHeaderValidator _blockHeaderValidator;

        [SetUp]
        public void Setup()
        {
            _blockchainStore = Substitute.For<IBlockchainStore>();
            _blockchainStore.FindBlock(_grandgrandparent.Hash).Returns(new Block(_grandgrandparent));
            _blockchainStore.FindBlock(_grandparent.Hash).Returns(new Block(_grandparent));
            _blockchainStore.FindBlock(_parent.Hash).Returns(new Block(_parent));
            _blockchainStore.FindBlock(_header.Hash).Returns(new Block(_header));
            
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

            OmmersValidator ommersValidator = new OmmersValidator(_blockchainStore, blockHeaderValidator);
            Assert.False(ommersValidator.Validate(new BlockHeader(), ommers));
        }

        [Test]
        public void When_ommer_is_self_returns_false()
        {
            IBlockHeaderValidator blockHeaderValidator = Substitute.For<IBlockHeaderValidator>();
            blockHeaderValidator.Validate(Arg.Any<BlockHeader>()).Returns(true);

            BlockHeader[] ommers = new BlockHeader[1];
            ommers[0] = _header;

            OmmersValidator ommersValidator = new OmmersValidator(_blockchainStore, blockHeaderValidator);
            Assert.False(ommersValidator.Validate(_header, ommers));
        }

        [Test]
        public void When_ommer_is_brother_returns_false()
        {
            BlockHeader[] ommers = new BlockHeader[1];
            ommers[0] = new BlockHeader();
            ommers[0].ParentHash = _parent.Hash;
            ommers[0].Number = _header.Number;

            OmmersValidator ommersValidator = new OmmersValidator(_blockchainStore, _blockHeaderValidator);
            Assert.False(ommersValidator.Validate(_header, ommers));
        }

        [Test]
        public void When_ommer_is_father_returns_false()
        {
            BlockHeader[] ommers = new BlockHeader[1];
            ommers[0] = _parent;

            OmmersValidator ommersValidator = new OmmersValidator(_blockchainStore, _blockHeaderValidator);
            Assert.False(ommersValidator.Validate(_header, ommers));
        }

        [Test]
        public void When_ommer_was_already_included_return_false()
        {
            BlockHeader[] ommers = GetValidOmmers(1);
            _blockchainStore.FindOmmer(ommers[0].Hash).Returns(ommers[0]);

            OmmersValidator ommersValidator = new OmmersValidator(_blockchainStore, _blockHeaderValidator);
            Assert.False(ommersValidator.Validate(_header, ommers));
        }

        private BlockHeader[] GetValidOmmers(int count)
        {
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

            OmmersValidator ommersValidator = new OmmersValidator(_blockchainStore, _blockHeaderValidator);
            Assert.True(ommersValidator.Validate(_header, ommers));
        }
        
        [Test]
        public void Grandpas_brother_is_fine()
        {
            BlockHeader[] ommers = GetValidOmmers(1);
            ommers[0].Number = _grandparent.Number;
            ommers[0].ParentHash = _grandgrandparent.Hash;

            OmmersValidator ommersValidator = new OmmersValidator(_blockchainStore, _blockHeaderValidator);
            Assert.True(ommersValidator.Validate(_header, ommers));
        }

        [Test]
        public void Same_ommer_twice_returns_false()
        {
            BlockHeader[] ommers = GetValidOmmers(1).Union(GetValidOmmers(1)).ToArray();

            OmmersValidator ommersValidator = new OmmersValidator(_blockchainStore, _blockHeaderValidator);
            Assert.False(ommersValidator.Validate(_header, ommers));
        }
    }
}