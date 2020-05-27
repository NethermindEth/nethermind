using Nethermind.Abi;
using Nethermind.Blockchain.Filters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Facade;
using Nethermind.JsonRpc.Modules.Baseline;
using Nethermind.Logging;
using Nethermind.TxPool;
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
        private Keccak _keccak;
        private Address _adressMock;
        private Address _adressMock2;
        private FilterLog filterLog;

        [SetUp]
        public void SetUp()
        {
            _abiEncoderMock = Substitute.For<IAbiEncoder>();
            _loggerMock = Substitute.For<ILogManager>();
            _txPoolBridgeMock = Substitute.For<ITxPoolBridge>();
            _baselineModule = new BaselineModule(_txPoolBridgeMock, _abiEncoderMock, _loggerMock);
            _keccak = new Keccak("0xf23682e2f2e9ea141d4663defc40f72a76c35b35d8cad6e0161901f2a967c9b6");
            _adressMock = Substitute.For<Address>(_keccak);
            _adressMock2 = Substitute.For<Address>(_keccak);

            filterLog = new FilterLog(0, 
                0, 
                5, 
                new Keccak("0xbbf3682375dae572acfb63c67f862dcdf59e96e043d44152cca7ebefa8c14cec"), 
                0,
                new Keccak("0xbe45ba4ec5fdfa14239c5e345f7e99dc7f7a6d6cd05e7e52b1fc5254bc712b9b"),
                new Address("0x83c82edd1605ac37d9065d784fdc000b20e9879d"),
                new byte[] {1,1,1},
                new Keccak[] { new Keccak("0x6a82ba2aa1d2c039c41e6e2b5a5a1090d09906f060d32af9c1ac0beff7af75c0")});
        }

        [Test]
        public void baseline_insert_leaf_sends_transaction()
        {
            _abiEncoderMock.Encode(Arg.Any<AbiEncodingStyle>(), Arg.Any<AbiSignature>(), Arg.Any<Keccak>()).Returns(new byte[] {1,1,1,1,1,11,1});
            _baselineModule.baseline_insertLeaf(_adressMock, _adressMock2, _keccak);

            _txPoolBridgeMock.Received(1).SendTransaction(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
        }

        [Test]
        public void insert_leaf_given_hash_is_part_of_log_filter_data()
        {
            var dataBytes = Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000000f23682e2f2e9ea141d4663defc40f72a76c35b35d8cad6e0161901f2a967c9b61ace302d4fce7493773820dd2a7ecb84a16c199ff2607af77adff00000000000");

            var keccakBytes = Bytes.FromHexString("f23682e2f2e9ea141d4663defc40f72a76c35b35d8cad6e0161901f2a967c9b6");
            var keccak = new Keccak(keccakBytes);
            _baselineModule.baseline_insertLeaf(_adressMock, _adressMock2, keccak);
            Assert.Contains(keccakBytes, dataBytes);
        }

    }
}