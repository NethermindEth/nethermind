using Nevermind.Blockchain;
using Nevermind.Blockchain.Validators;
using NSubstitute;
using NUnit.Framework;

namespace Nevermind.Core.Test
{
    [TestFixture]
    [Ignore("not yet ready")]
    public class BlockValidatorTests
    {
        [Test]
        public void Test()
        {
            IBlockStore blockchain = Substitute.For<IBlockStore>();
            
            BlockHeaderValidator blockHeaderValidator = new BlockHeaderValidator(blockchain);
            OmmersValidator ommersValidator = new OmmersValidator(blockchain, blockHeaderValidator);
            BlockValidator blockValidator = new BlockValidator(blockHeaderValidator, ommersValidator, null);
            
            
        }
    }
}