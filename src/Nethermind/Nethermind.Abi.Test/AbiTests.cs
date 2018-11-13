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

using System.Numerics;
using System.Text;
using MathNet.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Abi.Test
{
    [TestFixture]
    public class AbiTests
    {
        private readonly AbiEncoder _abiEncoder = new AbiEncoder();

        [Test]
        public void Dynamic_array_of_dynamic_array_of_uint()
        {
            AbiType type = new AbiArray(new AbiArray(AbiType.UInt));
            AbiSignature signature = new AbiSignature("abc", type);
            BigInteger[] element = {1, 2, 3};
            BigInteger[][] data = {element, element};
            byte[] encoded = _abiEncoder.Encode(signature, new object[] {data});
            object[] arguments = _abiEncoder.Decode(signature, encoded);
            Assert.AreEqual(arguments[0], data);
        }

        [Test]
        public void Dynamic_array_of_dynamic_array_of_uint_empty()
        {
            AbiType type = new AbiArray(new AbiArray(AbiType.UInt));
            AbiSignature signature = new AbiSignature("abc", type);
            BigInteger[] data = { };
            byte[] encoded = _abiEncoder.Encode(signature, data);
            object[] arguments = _abiEncoder.Decode(signature, encoded);
            Assert.AreEqual(arguments[0], data);
        }

        [Test]
        public void Dynamic_array_of_string()
        {
            AbiType type = new AbiArray(AbiType.String);
            AbiSignature signature = new AbiSignature("abc", type);
            string[] data = {"a", "bc", "def"};
            byte[] encoded = _abiEncoder.Encode(signature, new object[] {data});
            object[] arguments = _abiEncoder.Decode(signature, encoded);
            Assert.AreEqual(arguments[0], data);
        }

        [Test]
        public void Dynamic_array_of_uint()
        {
            AbiType type = new AbiArray(AbiType.UInt);
            AbiSignature signature = new AbiSignature("abc", type);
            BigInteger[] data = {1, 2, 3};
            byte[] encoded = _abiEncoder.Encode(signature, data);
            object[] arguments = _abiEncoder.Decode(signature, encoded);
            Assert.AreEqual(arguments[0], data);
        }

        [Test]
        public void Fixed_array_of_fixed_array_of_uint()
        {
            AbiType type = new AbiFixedLengthArray(new AbiFixedLengthArray(AbiType.UInt, 2), 3);
            BigInteger[] element = {1, 1};
            BigInteger[][] data = {element, element, element};
            AbiSignature signature = new AbiSignature("abc", type);
            byte[] encoded = _abiEncoder.Encode(signature, new object[] {data});
            object[] arguments = _abiEncoder.Decode(signature, encoded);
            Assert.AreEqual(arguments[0], data);
        }

        [Test]
        public void Fixed_array_of_string()
        {
            AbiType type = new AbiFixedLengthArray(AbiType.String, 3);
            AbiSignature signature = new AbiSignature("abc", type);
            string[] data = {"a", "bc", "def"};
            byte[] encoded = _abiEncoder.Encode(signature, new object[] {data});
            object[] arguments = _abiEncoder.Decode(signature, encoded);
            Assert.AreEqual(arguments[0], data);
        }

        [Test]
        public void Fixed_array_of_uint()
        {
            AbiType type = new AbiFixedLengthArray(AbiType.UInt, 2);
            BigInteger[] data = {1, 1};
            AbiSignature signature = new AbiSignature("abc", type);
            byte[] encoded = _abiEncoder.Encode(signature, data);
            object[] arguments = _abiEncoder.Decode(signature, encoded);
            Assert.AreEqual(arguments[0], data);
        }

        [Test]
        public void Test_bytes()
        {
            AbiType type = new AbiBytes(19);
            byte[] data = new byte[] {1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19};
            AbiSignature signature = new AbiSignature("abc", type);
            byte[] encoded = _abiEncoder.Encode(signature, data);
            object[] arguments = _abiEncoder.Decode(signature, encoded);
            Assert.True(Bytes.AreEqual((byte[]) arguments[0], data));
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
        public void Test_dynamic_bytes()
        {
            AbiType type = AbiType.DynamicBytes;
            byte[] data = new byte[17] {1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17};
            AbiSignature signature = new AbiSignature("abc", type);
            byte[] encoded = _abiEncoder.Encode(signature, data);
            object[] arguments = _abiEncoder.Decode(signature, encoded);
            Assert.True(Bytes.AreEqual((byte[]) arguments[0], data));
        }

        [Test]
        public void Test_fixed()
        {
            AbiFixed type = AbiType.Fixed;
            BigRational data = BigRational.FromBigInt(123456789) * BigRational.Reciprocal(BigRational.Pow(BigRational.FromInt(10), type.Precision));
            AbiSignature signature = new AbiSignature("abc", type);
            byte[] encoded = _abiEncoder.Encode(signature, data);
            object[] arguments = _abiEncoder.Decode(signature, encoded);
            Assert.AreEqual(arguments[0], data);
        }

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
            Assert.True(Bytes.AreEqual((byte[]) arguments[0], data));
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
        public void Test_single_uint()
        {
            AbiType type = AbiType.UInt;
            AbiSignature signature = new AbiSignature("abc", type);
            byte[] encoded = _abiEncoder.Encode(signature, BigInteger.Zero);
            object[] arguments = _abiEncoder.Decode(signature, encoded);
            Assert.AreEqual(BigInteger.Zero, arguments[0]);
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

        [Test]
        public void Test_ufixed()
        {
            AbiUFixed type = AbiType.UFixed;
            
            BigRational data = BigRational.FromBigInt(-123456789) * BigRational.Reciprocal(BigRational.Pow(BigRational.FromInt(10), type.Precision));
            AbiSignature signature = new AbiSignature("abc", type);
            byte[] encoded = _abiEncoder.Encode(signature, data);
            object[] arguments = _abiEncoder.Decode(signature, encoded);
            Assert.AreEqual(arguments[0], data);
        }

        /// <summary>
        ///     http://solidity.readthedocs.io/en/develop/abi-spec.html
        /// </summary>
        [Test]
        public void Tutorial_test()
        {
            byte[] expectedValue = Bytes.FromHexString(
                "0x8be65246" +
                "0000000000000000000000000000000000000000000000000000000000000123" +
                "0000000000000000000000000000000000000000000000000000000000000080" +
                "3132333435363738393000000000000000000000000000000000000000000000" +
                "00000000000000000000000000000000000000000000000000000000000000e0" +
                "0000000000000000000000000000000000000000000000000000000000000002" +
                "0000000000000000000000000000000000000000000000000000000000000456" +
                "0000000000000000000000000000000000000000000000000000000000000789" +
                "000000000000000000000000000000000000000000000000000000000000000d" +
                "48656c6c6f2c20776f726c642100000000000000000000000000000000000000");

            AbiSignature signature = new AbiSignature(
                "f",
                AbiType.UInt,
                new AbiArray(new AbiUInt(32)),
                new AbiBytes(10),
                AbiType.DynamicBytes);
            byte[] encoded = _abiEncoder.Encode(
                signature,
                new BigInteger(0x123),
                new BigInteger[] {0x456, 0x789},
                Encoding.ASCII.GetBytes("1234567890"),
                Encoding.ASCII.GetBytes("Hello, world!"));
            Assert.True(Bytes.AreEqual(expectedValue, encoded));
        }
    }
}