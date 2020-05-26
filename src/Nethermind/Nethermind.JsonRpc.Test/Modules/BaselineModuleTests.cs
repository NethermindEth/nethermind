using Nethermind.Abi;
using Nethermind.Facade;
using Nethermind.JsonRpc.Modules.Baseline;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    [TestFixture]
    public class BaselineModuleTests
    {
        private IAbiEncoder _abiEncoderMock;
        private ILogManager _loggerMock;
        private ITxPoolBridge _txPoolBridgeMock;
        private BaselineModule _baselineModule;


        [SetUp]
        public void SetUp()
        {
            _abiEncoderMock = Substitute.For<IAbiEncoder>();
            _loggerMock = Substitute.For<ILogManager>();
            _txPoolBridgeMock = Substitute.For<ITxPoolBridge>();

            _baselineModule = new BaselineModule(_txPoolBridgeMock, _abiEncoderMock, _loggerMock);
        }

        [Test]
        public void TestExample()
        {
            var result = _baselineModule.baseline_getSiblings();
            Assert.IsNull(result);
        }

        [Test]
        public void baseline_insert_leaf_sends_transaction()
        {
            string expectedEncodedHash = "0x0000000000000000000000000000000000000000000000000000000000000000f23682e2f2e9ea141d4663defc40f72a76c35b35d8cad6e0161901f2a967c9b61ace302d4fce7493773820dd2a7ecb84a16c199ff2607af77adff00000000000";

            _abiEncoderMock.Encode(Arg.Any<AbiEncodingStyle>(), Arg.Any<AbiSignature>(), Arg.Any<Keccak>()).Returns(expectedEncodedHash);
            _baselineModule.baseline_insertLeaf(new Address(), new Address(), new Keccak());

            _txPoolBridgeMock.Received(1).SendTransaction(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
        }

    }
}