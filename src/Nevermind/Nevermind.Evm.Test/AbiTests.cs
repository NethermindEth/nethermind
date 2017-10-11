using Nevermind.Core.Sugar;
using Nevermind.Evm.Abi;
using NUnit.Framework;

namespace Nevermind.Evm.Test
{
    [TestFixture]
    public class AbiTests
    {
        private readonly AbiEncoder _abiEncoder = new AbiEncoder();

        [Test]
        public void Test_single_address()
        {
            byte[] data = new byte[20];
            AbiSignature signature = new AbiSignature("abc", AbiType.Address);
            byte[] encoded = _abiEncoder.Encode(signature, data);
            byte[][] arguments = _abiEncoder.Decode(signature, encoded);
            Assert.True(Bytes.UnsafeCompare(arguments[0], data));
        }

        [Test]
        public void Test_single_bool()
        {
            byte[] data = new byte[1];
            AbiSignature signature = new AbiSignature("abc", AbiType.Bool);
            byte[] encoded = _abiEncoder.Encode(signature, data);
            byte[][] arguments = _abiEncoder.Decode(signature, encoded);
            Assert.True(Bytes.UnsafeCompare(arguments[0], data));
        }

        [Test]
        public void Test_single_function()
        {
            byte[] data = new byte[24];
            AbiSignature signature = new AbiSignature("abc", AbiType.Function);
            byte[] encoded = _abiEncoder.Encode(signature, data);
            byte[][] arguments = _abiEncoder.Decode(signature, encoded);
            Assert.True(Bytes.UnsafeCompare(arguments[0], data));
        }

        [Test]
        public void Test_single_uint()
        {
            byte[] data = new byte[32];
            AbiSignature signature = new AbiSignature("abc", AbiType.UInt);
            byte[] encoded = _abiEncoder.Encode(signature, data);
            byte[][] arguments = _abiEncoder.Decode(signature, encoded);
            Assert.True(Bytes.UnsafeCompare(arguments[0], data));
        }
    }
}