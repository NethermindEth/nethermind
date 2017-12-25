using Nevermind.Blockchain;
using Nevermind.Blockchain.Validators;
using Nevermind.Core.Potocol;
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
            IEthereumRelease spec = Olympic.Instance;
            IBlockStore blockchain = Substitute.For<IBlockStore>();

            BlockHeaderValidator blockHeaderValidator = new BlockHeaderValidator(blockchain);
            OmmersValidator ommersValidator = new OmmersValidator(blockchain, blockHeaderValidator);
            SignatureValidator signatureValidator = new SignatureValidator(spec, ChainId.MainNet);
            TransactionValidator transactionValidator = new TransactionValidator(spec, signatureValidator);
            BlockValidator blockValidator = new BlockValidator(transactionValidator, blockHeaderValidator, ommersValidator, null);
        }
    }
}