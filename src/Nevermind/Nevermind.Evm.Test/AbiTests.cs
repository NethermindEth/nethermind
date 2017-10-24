using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Encoding;
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
            AbiType type = AbiType.Address;
            AbiSignature signature = new AbiSignature("abc", type);
            Address arg = new Address(Keccak.OfAnEmptyString);
            byte[] encoded = _abiEncoder.Encode(signature, arg);
            object[] arguments = _abiEncoder.Decode(signature, encoded);
            Assert.AreEqual(arguments[0], arg);
        }

        [Test]
        public void Test_single_bool()
        {
            AbiType type = AbiType.Bool;
            AbiSignature signature = new AbiSignature("abc", type);
            byte[] encoded = _abiEncoder.Encode(signature, true);
            object[] arguments = _abiEncoder.Decode(signature, encoded);
            Assert.AreEqual(true, arguments[0]);
        }

        [Test]
        public void Test_single_function()
        {
            AbiType type = AbiType.Function;
            byte[] data = new byte[24];
            AbiSignature signature = new AbiSignature("abc", type);
            byte[] encoded = _abiEncoder.Encode(signature, data);
            object[] arguments = _abiEncoder.Decode(signature, encoded);
            byte[] firstEncoded = type.Encode(arguments[0]);
            Assert.True(Bytes.UnsafeCompare(firstEncoded, data));
        }

        [Test]
        public void Test_single_uint()
        {
            AbiType type = AbiType.UInt;
            AbiSignature signature = new AbiSignature("abc", type);
            byte[] encoded = _abiEncoder.Encode(signature, BigInteger.Zero);
            object[] arguments = _abiEncoder.Decode(signature, encoded);
            Assert.AreEqual(BigInteger.Zero, arguments[0]);
        }

        [Test]
        public void Test_single_int()
        {
            AbiType type = AbiType.Int;
            AbiSignature signature = new AbiSignature("abc", type);
            byte[] encoded = _abiEncoder.Encode(signature, BigInteger.MinusOne);
            object[] arguments = _abiEncoder.Decode(signature, encoded);
            Assert.AreEqual(BigInteger.MinusOne, arguments[0]);
        }

        [Test]
        public void Test_dynamic_bytes()
        {
            AbiType type = AbiType.Bytes;
            byte[] data = new byte[17];
            AbiSignature signature = new AbiSignature("abc", type);
            byte[] encoded = _abiEncoder.Encode(signature, data);
            object[] arguments = _abiEncoder.Decode(signature, encoded);
            Assert.True(Bytes.UnsafeCompare((byte[])arguments[0], data));
        }

        [Test]
        public void Test_bytes()
        {
            AbiType type = new AbiBytes(19);
            byte[] data = new byte[19];
            AbiSignature signature = new AbiSignature("abc", type);
            byte[] encoded = _abiEncoder.Encode(signature, data);
            object[] arguments = _abiEncoder.Decode(signature, encoded);
            Assert.True(Bytes.UnsafeCompare((byte[])arguments[0], data));
        }

        [Test]
        public void Test_bytes_invalid_length()
        {
            AbiType type = new AbiBytes(19);
            byte[] data = new byte[23];
            AbiSignature signature = new AbiSignature("abc", type);
            Assert.Throws<AbiException>(() => _abiEncoder.Encode(signature, data));
        }

        [Test]
        public void Test_string()
        {
            AbiType type = AbiType.String;
            string data = "def";
            AbiSignature signature = new AbiSignature("abc", type);
            byte[] encoded = _abiEncoder.Encode(signature, data);
            object[] arguments = _abiEncoder.Decode(signature, encoded);
            Assert.AreEqual(arguments[0], data);
        }
    }
}