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

using System;
using System.Numerics;
using System.Text;
using MathNet.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Abi.Test
{
    [TestFixture]
    public class AbiTests
    {
        private readonly AbiEncoder _abiEncoder = AbiEncoder.Instance;

        [TestCase(AbiEncodingStyle.IncludeSignature)]
        [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.None)]
        public void Dynamic_array_of_dynamic_array_of_uint(AbiEncodingStyle encodingStyle)
        {
            AbiType type = new AbiArray(new AbiArray(AbiType.UInt256));
            AbiSignature signature = new AbiSignature("abc", type);
            UInt256[] element = {1, 2, 3};
            UInt256[][] data = {element, element};
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, new object[] {data});
            object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.AreEqual(data, arguments[0]);
        }

        [TestCase(AbiEncodingStyle.IncludeSignature)]
        [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.None)]
        public void Dynamic_array_of_dynamic_array_of_uint_empty(AbiEncodingStyle encodingStyle)
        {
            AbiType type = new AbiArray(new AbiArray(AbiType.UInt256));
            AbiSignature signature = new AbiSignature("abc", type);
            BigInteger[] data = { };
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, data);
            object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.AreEqual(arguments[0], data);
        }

        [TestCase(AbiEncodingStyle.IncludeSignature)]
        [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.None)]
        public void Dynamic_array_of_string(AbiEncodingStyle encodingStyle)
        {
            AbiType type = new AbiArray(AbiType.String);
            AbiSignature signature = new AbiSignature("abc", type);
            string[] data = {"a", "bc", "def"};
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, new object[] {data});
            object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.AreEqual(data, arguments[0]);
        }

        [TestCase(AbiEncodingStyle.IncludeSignature)]
        [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.None)]
        public void Dynamic_array_of_uint(AbiEncodingStyle encodingStyle)
        {
            AbiType type = new AbiArray(AbiType.UInt256);
            AbiSignature signature = new AbiSignature("abc", type);
            UInt256[] data = {1, 2, 3};
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, data);
            object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.AreEqual(data, arguments[0]);
        }

        [TestCase(AbiEncodingStyle.IncludeSignature)]
        [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.None)]
        public void Fixed_array_of_fixed_array_of_uint(AbiEncodingStyle encodingStyle)
        {
            AbiType type = new AbiFixedLengthArray(new AbiFixedLengthArray(AbiType.UInt256, 2), 3);
            UInt256[] element = {1, 1};
            UInt256[][] data = {element, element, element};
            AbiSignature signature = new AbiSignature("abc", type);
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, new object[] {data});
            object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.AreEqual(data, arguments[0]);
        }

        [TestCase(AbiEncodingStyle.IncludeSignature)]
        [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.None)]
        public void Fixed_array_of_string(AbiEncodingStyle encodingStyle)
        {
            AbiType type = new AbiFixedLengthArray(AbiType.String, 3);
            AbiSignature signature = new AbiSignature("abc", type);
            string[] data = {"a", "bc", "def"};
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, new object[] {data});
            object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.AreEqual(data, arguments[0]);
        }

        [TestCase(AbiEncodingStyle.IncludeSignature)]
        [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.None)]
        public void Fixed_array_of_uint(AbiEncodingStyle encodingStyle)
        {
            AbiType type = new AbiFixedLengthArray(AbiType.UInt256, 2);
            UInt256[] data = {1, 1};
            AbiSignature signature = new AbiSignature("abc", type);
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, data);
            object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.AreEqual(data, arguments[0]);
        }

        [TestCase(AbiEncodingStyle.IncludeSignature)]
        [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.None)]
        public void Test_bytes(AbiEncodingStyle encodingStyle)
        {
            AbiType type = new AbiBytes(19);
            byte[] data = new byte[] {1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19};
            AbiSignature signature = new AbiSignature("abc", type);
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, data);
            object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.True(Bytes.AreEqual((byte[]) arguments[0], data));
        }

        [TestCase(AbiEncodingStyle.IncludeSignature)]
        [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.None)]
        public void Test_bytes_invalid_length(AbiEncodingStyle encodingStyle)
        {
            AbiType type = new AbiBytes(19);
            byte[] data = new byte[23];
            AbiSignature signature = new AbiSignature("abc", type);
            Assert.Throws<AbiException>(() => _abiEncoder.Encode(encodingStyle, signature, data));
        }

        [TestCase(AbiEncodingStyle.IncludeSignature)]
        [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.None)]
        public void Test_dynamic_bytes(AbiEncodingStyle encodingStyle)
        {
            AbiType type = AbiType.DynamicBytes;
            byte[] data = new byte[17] {1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17};
            AbiSignature signature = new AbiSignature("abc", type);
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, data);
            object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.True(Bytes.AreEqual((byte[]) arguments[0], data));
        }

        [TestCase(AbiEncodingStyle.IncludeSignature)]
        [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.None)]
        public void Test_fixed(AbiEncodingStyle encodingStyle)
        {
            AbiFixed type = AbiType.Fixed;
            BigRational data = BigRational.FromBigInt(123456789) * BigRational.Reciprocal(BigRational.Pow(BigRational.FromInt(10), type.Precision));
            AbiSignature signature = new AbiSignature("abc", type);
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, data);
            object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.AreEqual(data, arguments[0]);
        }

        [TestCase(AbiEncodingStyle.IncludeSignature)]
        [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.None)]
        public void Test_single_address(AbiEncodingStyle encodingStyle)
        {
            AbiType type = AbiType.Address;
            AbiSignature signature = new AbiSignature("abc", type);
            Address arg = new Address(Keccak.OfAnEmptyString);
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, arg);
            object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.AreEqual(arg, arguments[0]);
        }

        [TestCase(AbiEncodingStyle.IncludeSignature)]
        [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.None)]
        public void Test_single_bool(AbiEncodingStyle encodingStyle)
        {
            AbiType type = AbiType.Bool;
            AbiSignature signature = new AbiSignature("abc", type);
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, true);
            object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.AreEqual(true, arguments[0]);
        }

        [TestCase(AbiEncodingStyle.IncludeSignature)]
        [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.None)]
        public void Test_single_function(AbiEncodingStyle encodingStyle)
        {
            AbiType type = AbiType.Function;
            byte[] data = new byte[24];
            AbiSignature signature = new AbiSignature("abc", type);
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, data);
            object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.True(Bytes.AreEqual((byte[]) arguments[0], data));
        }

        [TestCase(AbiEncodingStyle.IncludeSignature)]
        [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.None)]
        public void Test_single_int(AbiEncodingStyle encodingStyle)
        {
            AbiType type = AbiType.Int256;
            AbiSignature signature = new AbiSignature("abc", type);
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, BigInteger.MinusOne);
            object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.AreEqual(BigInteger.MinusOne, arguments[0]);
        }
        
        [TestCase(AbiEncodingStyle.None)]
        public void Test_single_uint_with_casting(AbiEncodingStyle encodingStyle)
        {
            AbiType type = AbiType.UInt256;
            AbiSignature signature = new AbiSignature("abc", type);
            
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, UInt256.One);
            object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.AreEqual(UInt256.One, arguments[0]);
            
            encoded = _abiEncoder.Encode(encodingStyle, signature, 1L);
            arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.AreEqual(UInt256.One, arguments[0]);
            
            encoded = _abiEncoder.Encode(encodingStyle, signature, 1UL);
            arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.AreEqual(UInt256.One, arguments[0]);
            
            encoded = _abiEncoder.Encode(encodingStyle, signature, 1);
            arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.AreEqual(UInt256.One, arguments[0]);
            
            encoded = _abiEncoder.Encode(encodingStyle, signature, 1U);
            arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.AreEqual(UInt256.One, arguments[0]);
        }

        [TestCase(AbiEncodingStyle.IncludeSignature)]
        [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.None)]
        public void Test_single_uint(AbiEncodingStyle encodingStyle)
        {
            AbiType type = AbiType.UInt256;
            AbiSignature signature = new AbiSignature("abc", type);
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, BigInteger.Zero);
            object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.AreEqual(UInt256.Zero, arguments[0]);
        }

        [TestCase(AbiEncodingStyle.IncludeSignature)]
        [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.None)]
        public void Test_single_uint32(AbiEncodingStyle encodingStyle)
        {
            AbiType type = new AbiUInt(32);
            AbiSignature signature = new AbiSignature("abc", type);
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, 123U);
            object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.AreEqual(123U, arguments[0]);
        }

        [TestCase(AbiEncodingStyle.IncludeSignature)]
        [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.None)]
        public void Test_string(AbiEncodingStyle encodingStyle)
        {
            AbiType type = AbiType.String;
            string data = "def";
            AbiSignature signature = new AbiSignature("abc", type);
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, data);
            object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.AreEqual(data, arguments[0]);
        }

        [TestCase(AbiEncodingStyle.IncludeSignature)]
        [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.None)]
        public void Test_ufixed(AbiEncodingStyle encodingStyle)
        {
            AbiUFixed type = AbiType.UFixed;

            BigRational data = BigRational.FromBigInt(-123456789) * BigRational.Reciprocal(BigRational.Pow(BigRational.FromInt(10), type.Precision));
            AbiSignature signature = new AbiSignature("abc", type);
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, data);
            object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.AreEqual(data, arguments[0]);
        }
        
        [TestCase(0, 0)]
        [TestCase(0, 19)]
        [TestCase(8, 0)]
        [TestCase(256 + 8, 19)]
        [TestCase(8, 128)]
        [TestCase(9, 8)]
        public void Test_ufixed_exception(int length, int precision)
        {
            Assert.Throws<ArgumentException>(() => _ = new AbiUFixed(length, precision));
        }
        
        [TestCase(0, 0)]
        [TestCase(0, 19)]
        [TestCase(8, 0)]
        [TestCase(256 + 8, 19)]
        [TestCase(8, 128)]
        [TestCase(9, 8)]
        public void Test_fixed_exception(int length, int precision)
        {
            Assert.Throws<ArgumentException>(() => _ = new AbiFixed(length, precision));
        }
        
        [TestCase(0)]
        [TestCase(7)]
        [TestCase(264)]
        public void Test_int_exception(int length)
        {
            Assert.Throws<ArgumentException>(() => _ = new AbiInt(length));
        }
        
        [TestCase(0)]
        [TestCase(7)]
        [TestCase(264)]
        public void Test_uint_exception(int length)
        {
            Assert.Throws<ArgumentException>(() => _ = new AbiUInt(length));
        }

        [TestCase(AbiEncodingStyle.IncludeSignature)]
        [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.None)]
        public void Test_single_address_no_signature(AbiEncodingStyle encodingStyle)
        {
            AbiType type = AbiType.Address;
            AbiSignature signature = new AbiSignature("abc", type);
            Address arg = new Address(Keccak.OfAnEmptyString);
            byte[] encoded = _abiEncoder.Encode(AbiEncodingStyle.None, signature, arg);
            object[] arguments = _abiEncoder.Decode(AbiEncodingStyle.None, signature, encoded);
            Assert.AreEqual(arg, arguments[0]);
        }

        [TestCase(AbiEncodingStyle.IncludeSignature)]
        [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.None)]
        public void Test_packed(AbiEncodingStyle encodingStyle)
        {
            Keccak assetId = Keccak.Compute("assetId");
            uint expiryTime = (uint) Timestamper.Default.UnixTime.Seconds + 86000;
            UInt256 value = 1.Ether();
            uint units = 10U;
            byte[] salt = new byte[16];

            AbiSignature abiDef = new AbiSignature("example",
                new AbiBytes(32),
                new AbiUInt(32),
                new AbiUInt(96),
                new AbiUInt(32),
                new AbiBytes(16),
                AbiType.Address,
                AbiType.Address);

            byte[] encoded = _abiEncoder.Encode(AbiEncodingStyle.Packed, abiDef, assetId.Bytes, units, value, expiryTime, salt, Address.Zero, Address.Zero);
            Assert.AreEqual(108, encoded.Length);
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
                AbiType.UInt256,
                new AbiArray(new AbiUInt(32)),
                new AbiBytes(10),
                AbiType.DynamicBytes);
            byte[] encoded = _abiEncoder.Encode(
                AbiEncodingStyle.IncludeSignature,
                signature,
                new BigInteger(0x123),
                new BigInteger[] {0x456, 0x789},
                Encoding.ASCII.GetBytes("1234567890"),
                Encoding.ASCII.GetBytes("Hello, world!"));
            Assert.True(Bytes.AreEqual(expectedValue, encoded));
        }
    }
}
