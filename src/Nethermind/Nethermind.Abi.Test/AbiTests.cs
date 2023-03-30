// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using FluentAssertions;
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
            UInt256[] element = { 1, 2, 3 };
            UInt256[][] data = { element, element };
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, new object[] { data });
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
            string[] data = { "a", "bc", "def" };
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, new object[] { data });
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
            UInt256[] data = { 1, 2, 3 };
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
            UInt256[] element = { 1, 1 };
            UInt256[][] data = { element, element, element };
            AbiSignature signature = new AbiSignature("abc", type);
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, new object[] { data });
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
            string[] data = { "a", "bc", "def" };
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, new object[] { data });
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
            UInt256[] data = { 1, 1 };
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
            byte[] data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19 };
            AbiSignature signature = new AbiSignature("abc", type);
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, data);
            object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.True(Bytes.AreEqual((byte[])arguments[0], data));
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
            byte[] data = new byte[17] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17 };
            AbiSignature signature = new AbiSignature("abc", type);
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, data);
            object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.True(Bytes.AreEqual((byte[])arguments[0], data));
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
            Assert.True(Bytes.AreEqual((byte[])arguments[0], data));
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
            uint expiryTime = (uint)Timestamper.Default.UnixTime.Seconds + 86000;
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

        [TestCase(AbiEncodingStyle.IncludeSignature)]
        [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.None)]
        public void Static_tuple(AbiEncodingStyle encodingStyle)
        {
            AbiType type = new AbiTuple(AbiType.UInt256, AbiType.Address, AbiType.Bool);

            AbiSignature signature = new AbiSignature("abc", type);

            ValueTuple<UInt256, Address, bool> staticTuple = new ValueTuple<UInt256, Address, bool>((UInt256)1000, Address.SystemUser, true);
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, staticTuple);
            object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.AreEqual(staticTuple, arguments[0]);
        }

        [TestCase(AbiEncodingStyle.IncludeSignature)]
        [TestCase(AbiEncodingStyle.None)]
        public void Dynamic_tuple(AbiEncodingStyle encodingStyle)
        {
            AbiType type = new AbiTuple(AbiType.DynamicBytes, AbiType.Address, AbiType.DynamicBytes);

            AbiSignature signature = new AbiSignature("abc", type);

            ValueTuple<byte[], Address, byte[]> dynamicTuple = new ValueTuple<byte[], Address, byte[]>(Bytes.FromHexString("0x004749fa3d"), Address.SystemUser, Bytes.Zero32);
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, dynamicTuple);
            object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.AreEqual(dynamicTuple, arguments[0]);
        }


        [TestCase(AbiEncodingStyle.IncludeSignature)]
        [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.None)]
        public void Multiple_params_with_one_of_them_a_tuple(AbiEncodingStyle encodingStyle)
        {
            AbiType type = new AbiTuple(AbiType.UInt256, AbiType.Address, AbiType.Bool);

            AbiSignature signature = new AbiSignature("abc", type, AbiType.String);

            ValueTuple<UInt256, Address, bool> staticTuple = new ValueTuple<UInt256, Address, bool>((UInt256)1000, Address.SystemUser, true);
            const string stringParam = "hello there!";
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, staticTuple, stringParam);
            object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.AreEqual(staticTuple, arguments[0]);
            Assert.AreEqual(stringParam, arguments[1]);
        }

        [TestCase(AbiEncodingStyle.IncludeSignature)]
        [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.None)]
        public void Multiple_params_with_one_of_them_a_tuple_dynamic_first(AbiEncodingStyle encodingStyle)
        {
            AbiType type = new AbiTuple(AbiType.UInt256, AbiType.Address, AbiType.Bool);

            AbiSignature signature = new AbiSignature("abc", AbiType.String, type);

            ValueTuple<UInt256, Address, bool> staticTuple = new ValueTuple<UInt256, Address, bool>((UInt256)1000, Address.SystemUser, true);
            const string stringParam = "hello there!";
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, stringParam, staticTuple);
            object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.AreEqual(stringParam, arguments[0]);
            Assert.AreEqual(staticTuple, arguments[1]);
        }

        [TestCase(AbiEncodingStyle.IncludeSignature)]
        [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.None)]
        public void Tuple_with_inner_static_tuple(AbiEncodingStyle encodingStyle)
        {
            AbiType type = new AbiTuple(AbiType.UInt256, new AbiTuple(AbiType.UInt256, AbiType.Address), AbiType.Bool);

            AbiSignature signature = new AbiSignature("abc", type);

            ValueTuple<UInt256, ValueTuple<UInt256, Address>, bool> staticTuple = new ValueTuple<UInt256, ValueTuple<UInt256, Address>, bool>((UInt256)1000, new ValueTuple<UInt256, Address>((UInt256)400, Address.SystemUser), true);
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, staticTuple);
            object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.AreEqual(staticTuple, arguments[0]);
        }

        [TestCase(AbiEncodingStyle.IncludeSignature)]
        [TestCase(AbiEncodingStyle.None)]
        public void Tuple_with_inner_dynamic_tuple(AbiEncodingStyle encodingStyle)
        {
            AbiType type = new AbiTuple(AbiType.UInt256, new AbiTuple(AbiType.DynamicBytes, AbiType.Address), AbiType.Bool);

            AbiSignature signature = new AbiSignature("abc", type);

            ValueTuple<UInt256, ValueTuple<byte[], Address>, bool> dynamicTuple = new ValueTuple<UInt256, ValueTuple<byte[], Address>, bool>((UInt256)1000, new ValueTuple<byte[], Address>(Bytes.FromHexString("0x019283fa3d"), Address.SystemUser), true);
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, dynamicTuple);
            object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.AreEqual(dynamicTuple, arguments[0]);
        }


        [TestCase(AbiEncodingStyle.IncludeSignature)]
        [TestCase(AbiEncodingStyle.None)]
        public void Dynamic_tuple_with_inner_dynamic_tuple(AbiEncodingStyle encodingStyle)
        {
            AbiType type = new AbiTuple(AbiType.DynamicBytes, new AbiTuple(AbiType.DynamicBytes, AbiType.Address), AbiType.Bool);

            AbiSignature signature = new AbiSignature("abc", type);

            ValueTuple<byte[], ValueTuple<byte[], Address>, bool> dynamicTuple = new ValueTuple<byte[], ValueTuple<byte[], Address>, bool>(Bytes.FromHexString("0x019283fa3d"), new ValueTuple<byte[], Address>(Bytes.FromHexString("0x019283fa3d"), Address.SystemUser), true);
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, dynamicTuple);
            object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.AreEqual(dynamicTuple, arguments[0]);
        }

        [TestCase(AbiEncodingStyle.IncludeSignature)]
        [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.Packed)]
        [TestCase(AbiEncodingStyle.None)]
        public void Tuple_with_inner_tuple_with_inner_tuple(AbiEncodingStyle encodingStyle)
        {
            AbiType type = new AbiTuple(new AbiTuple(new AbiTuple(AbiType.UInt256)));

            AbiSignature signature = new AbiSignature("abc", type);

            ValueTuple<ValueTuple<ValueTuple<UInt256>>> tupleception = new ValueTuple<ValueTuple<ValueTuple<UInt256>>>(new ValueTuple<ValueTuple<UInt256>>(new ValueTuple<UInt256>(88888)));
            byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, tupleception);
            object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
            Assert.AreEqual(tupleception, arguments[0]);
        }

        [Test]
        public void Can_decode_array_of_dynamic_tuples()
        {
            AbiType type = new AbiArray(new AbiTuple<UserOperationAbi>());
            AbiSignature signature = new AbiSignature("handleOps", type, AbiType.Address);

            object[] objects = _abiEncoder.Decode(AbiEncodingStyle.IncludeSignature, signature, Bytes.FromHexString("0x9984521800000000000000000000000000000000000000000000000000000000000000400000000000000000000000004173c8ce71a385e325357d8d79d6b7bc1c708f40000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000200000000000000000000000004ed7c70f96b99c776995fb64377f0d4ab3b0e1c10000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000018000000000000000000000000000000000000000000000000000000000000001a0000000000000000000000000000000000000000000000000000000000001a5b8000000000000000000000000000000000000000000000000000000000007a1200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000260000000000000000000000000fc7c490fc83e74556aa353ac360cf766e0d4313e000000000000000000000000000000000000000000000000000000000000028000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000084be6002c200000000000000000000000009635f643e140090a9a8dcd712ed6285858cebef0000000000000000000000000000000000000000000000000000000000000040000000000000000000000000000000000000000000000000000000000000000406661abd000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000041c0b5810722f6d3ff73d1e22ec2120670a6ae63ee916c026517a55754e7dd9a7b5d9b6aa5046bb35d009e034aace90845823e8365dbb22c2aa591fb60cd5c40001c00000000000000000000000000000000000000000000000000000000000000"));

            object[] expectedObjects = {
                new[] {new UserOperationAbi {
                    Target = new Address("0x4ed7c70F96B99c776995fB64377f0d4aB3B0e1C1"),
                    Nonce = UInt256.Zero,
                    InitCode = Bytes.Empty,
                    CallData = Bytes.FromHexString("0xbe6002c200000000000000000000000009635f643e140090a9a8dcd712ed6285858cebef0000000000000000000000000000000000000000000000000000000000000040000000000000000000000000000000000000000000000000000000000000000406661abd00000000000000000000000000000000000000000000000000000000"),
                    CallGas = 107960,
                    VerificationGas = 500000,
                    MaxFeePerGas = 0,
                    MaxPriorityFeePerGas = 0,
                    Paymaster = Address.Zero,
                    PaymasterData = Bytes.Empty,
                    Signer = new Address("0xFc7C490fc83e74556aa353ac360Cf766e0d4313e"),
                    Signature = Bytes.FromHexString("0xc0b5810722f6d3ff73d1e22ec2120670a6ae63ee916c026517a55754e7dd9a7b5d9b6aa5046bb35d009e034aace90845823e8365dbb22c2aa591fb60cd5c40001c")
                }},
                new Address("0x4173c8cE71a385e325357d8d79d6B7bc1c708F40")
            };

            objects.Should().BeEquivalentTo(expectedObjects);
        }

        [Test]
        public void Should_encode_arrays_and_lists_equally()
        {
            var abi = new AbiArray(AbiType.UInt256);
            var array = new UInt256[] { 1, 2, 3, UInt256.MaxValue };
            var list = new List<UInt256>() { 1, 2, 3, UInt256.MaxValue };

            abi.Encode(array, false).Should().BeEquivalentTo(abi.Encode(list, false));
        }

        private class UserOperationAbi
        {
            public Address Target { get; set; }
            public UInt256 Nonce { get; set; }
            public byte[] InitCode { get; set; }
            public byte[] CallData { get; set; }
            public UInt256 CallGas { get; set; }
            public UInt256 VerificationGas { get; set; }
            public UInt256 MaxFeePerGas { get; set; }
            public UInt256 MaxPriorityFeePerGas { get; set; }
            public Address Paymaster { get; set; }
            public byte[] PaymasterData { get; set; }
            public Address Signer { get; set; }
            public byte[] Signature { get; set; }
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
                new BigInteger[] { 0x456, 0x789 },
                Encoding.ASCII.GetBytes("1234567890"),
                Encoding.ASCII.GetBytes("Hello, world!"));
            encoded.ToHexString().Should().BeEquivalentTo(expectedValue.ToHexString());
        }
    }
}
