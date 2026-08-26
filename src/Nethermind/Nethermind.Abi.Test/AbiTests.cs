// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Text.Json;
using MathNet.Numerics;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Abi.Test;

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
        AbiSignature signature = new("abc", type);
        UInt256[] element = [1, 2, 3];
        UInt256[][] data = [element, element];
        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, [data]);
        object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(arguments[0], Is.EqualTo(data));
    }

    [TestCase(AbiEncodingStyle.IncludeSignature)]
    [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.None)]
    public void Dynamic_array_of_dynamic_array_of_uint_empty(AbiEncodingStyle encodingStyle)
    {
        AbiType type = new AbiArray(new AbiArray(AbiType.UInt256));
        AbiSignature signature = new("abc", type);
        BigInteger[] data = [];
        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, data);
        object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(data, Is.EqualTo(arguments[0]));
    }

    [TestCase(AbiEncodingStyle.IncludeSignature)]
    [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.None)]
    public void Dynamic_array_of_string(AbiEncodingStyle encodingStyle)
    {
        AbiType type = new AbiArray(AbiType.String);
        AbiSignature signature = new("abc", type);
        string[] data = ["a", "bc", "def"];
        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, [data]);
        object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(arguments[0], Is.EqualTo(data));
    }

    [TestCase(AbiEncodingStyle.IncludeSignature)]
    [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.None)]
    public void Dynamic_array_of_uint(AbiEncodingStyle encodingStyle)
    {
        AbiType type = new AbiArray(AbiType.UInt256);
        AbiSignature signature = new("abc", type);
        UInt256[] data = [1, 2, 3];
        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, data);
        object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(arguments[0], Is.EqualTo(data));
    }

    [TestCase(AbiEncodingStyle.IncludeSignature)]
    [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.None)]
    public void Fixed_array_of_fixed_array_of_uint(AbiEncodingStyle encodingStyle)
    {
        AbiType type = new AbiFixedLengthArray(new AbiFixedLengthArray(AbiType.UInt256, 2), 3);
        UInt256[] element = [1, 1];
        UInt256[][] data = [element, element, element];
        AbiSignature signature = new("abc", type);
        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, [data]);
        object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(arguments[0], Is.EqualTo(data));
    }

    [TestCase(AbiEncodingStyle.IncludeSignature)]
    [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.None)]
    public void Fixed_array_of_string(AbiEncodingStyle encodingStyle)
    {
        AbiType type = new AbiFixedLengthArray(AbiType.String, 3);
        AbiSignature signature = new("abc", type);
        string[] data = ["a", "bc", "def"];
        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, [data]);
        object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(arguments[0], Is.EqualTo(data));
    }

    [TestCase(AbiEncodingStyle.IncludeSignature)]
    [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.None)]
    public void Fixed_array_of_uint(AbiEncodingStyle encodingStyle)
    {
        AbiType type = new AbiFixedLengthArray(AbiType.UInt256, 2);
        UInt256[] data = [1, 1];
        AbiSignature signature = new("abc", type);
        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, data);
        object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(arguments[0], Is.EqualTo(data));
    }

    [TestCase(AbiEncodingStyle.IncludeSignature)]
    [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.None)]
    public void Test_bytes(AbiEncodingStyle encodingStyle)
    {
        AbiType type = new AbiBytes(19);
        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19];
        AbiSignature signature = new("abc", type);
        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, data);
        object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(Bytes.AreEqual((byte[])arguments[0], data), Is.True);
    }

    [TestCase(AbiEncodingStyle.IncludeSignature)]
    [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.None)]
    public void Test_bytes_invalid_length(AbiEncodingStyle encodingStyle)
    {
        AbiType type = new AbiBytes(19);
        byte[] data = new byte[23];
        AbiSignature signature = new("abc", type);
        Assert.Throws<AbiException>(() => _abiEncoder.Encode(encodingStyle, signature, data));
    }

    [TestCase(AbiEncodingStyle.IncludeSignature)]
    [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.None)]
    public void Test_dynamic_bytes(AbiEncodingStyle encodingStyle)
    {
        AbiType type = AbiType.DynamicBytes;
        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17];
        AbiSignature signature = new("abc", type);
        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, data);
        object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(Bytes.AreEqual((byte[])arguments[0], data), Is.True);
    }

    [TestCase(AbiEncodingStyle.IncludeSignature)]
    [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.None)]
    public void Test_fixed(AbiEncodingStyle encodingStyle)
    {
        AbiFixed type = AbiType.Fixed;
        BigRational data = BigRational.FromBigInt(123456789) * BigRational.Reciprocal(BigRational.Pow(BigRational.FromInt(10), type.Precision));
        AbiSignature signature = new("abc", type);
        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, data);
        object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(arguments[0], Is.EqualTo(data));
    }

    [TestCase(AbiEncodingStyle.IncludeSignature)]
    [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.None)]
    public void Test_single_address(AbiEncodingStyle encodingStyle)
    {
        AbiType type = AbiType.Address;
        AbiSignature signature = new("abc", type);
        Address arg = new(Keccak.OfAnEmptyString);
        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, arg);
        object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(arguments[0], Is.EqualTo(arg));
    }

    [TestCase(AbiEncodingStyle.IncludeSignature)]
    [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.None)]
    public void Test_single_bool(AbiEncodingStyle encodingStyle)
    {
        AbiType type = AbiType.Bool;
        AbiSignature signature = new("abc", type);
        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, true);
        object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(arguments[0], Is.EqualTo(true));
    }

    [TestCase(AbiEncodingStyle.IncludeSignature)]
    [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.None)]
    public void Test_single_function(AbiEncodingStyle encodingStyle)
    {
        AbiType type = AbiType.Function;
        byte[] data = new byte[24];
        AbiSignature signature = new("abc", type);
        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, data);
        object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(Bytes.AreEqual((byte[])arguments[0], data), Is.True);
    }

    [TestCase(AbiEncodingStyle.IncludeSignature)]
    [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.None)]
    public void Test_single_int(AbiEncodingStyle encodingStyle)
    {
        AbiType type = AbiType.Int256;
        AbiSignature signature = new("abc", type);
        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, BigInteger.MinusOne);
        object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(arguments[0], Is.EqualTo(BigInteger.MinusOne));
    }

    [TestCase(AbiEncodingStyle.None)]
    public void Test_single_uint_with_casting(AbiEncodingStyle encodingStyle)
    {
        AbiType type = AbiType.UInt256;
        AbiSignature signature = new("abc", type);

        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, UInt256.One);
        object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(arguments[0], Is.EqualTo(UInt256.One));

        encoded = _abiEncoder.Encode(encodingStyle, signature, 1L);
        arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(arguments[0], Is.EqualTo(UInt256.One));

        encoded = _abiEncoder.Encode(encodingStyle, signature, 1UL);
        arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(arguments[0], Is.EqualTo(UInt256.One));

        encoded = _abiEncoder.Encode(encodingStyle, signature, 1);
        arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(arguments[0], Is.EqualTo(UInt256.One));

        encoded = _abiEncoder.Encode(encodingStyle, signature, 1U);
        arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(arguments[0], Is.EqualTo(UInt256.One));
    }

    [TestCase(AbiEncodingStyle.IncludeSignature)]
    [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.None)]
    public void Test_single_uint(AbiEncodingStyle encodingStyle)
    {
        AbiType type = AbiType.UInt256;
        AbiSignature signature = new("abc", type);
        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, BigInteger.Zero);
        object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(arguments[0], Is.EqualTo(UInt256.Zero));
    }

    [TestCase(AbiEncodingStyle.IncludeSignature)]
    [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.None)]
    public void Test_single_uint32(AbiEncodingStyle encodingStyle)
    {
        AbiType type = new AbiUInt(32);
        AbiSignature signature = new("abc", type);
        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, 123U);
        object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(arguments[0], Is.EqualTo(123U));
    }

    [TestCase(AbiEncodingStyle.IncludeSignature)]
    [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.None)]
    public void Test_string(AbiEncodingStyle encodingStyle)
    {
        AbiType type = AbiType.String;
        string data = "def";
        AbiSignature signature = new("abc", type);
        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, data);
        object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(arguments[0], Is.EqualTo(data));
    }

    [TestCase(AbiEncodingStyle.IncludeSignature)]
    [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.None)]
    public void Test_ufixed(AbiEncodingStyle encodingStyle)
    {
        AbiUFixed type = AbiType.UFixed;

        BigRational data = BigRational.FromBigInt(-123456789) * BigRational.Reciprocal(BigRational.Pow(BigRational.FromInt(10), type.Precision));
        AbiSignature signature = new("abc", type);
        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, data);
        object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(arguments[0], Is.EqualTo(data));
    }

    [TestCase(0, 0)]
    [TestCase(0, 19)]
    [TestCase(8, 0)]
    [TestCase(256 + 8, 19)]
    [TestCase(8, 128)]
    [TestCase(9, 8)]
    public void Test_ufixed_exception(int length, int precision) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new AbiUFixed(length, precision));

    [TestCase(0, 0)]
    [TestCase(0, 19)]
    [TestCase(8, 0)]
    [TestCase(256 + 8, 19)]
    [TestCase(8, 128)]
    [TestCase(9, 8)]
    public void Test_fixed_exception(int length, int precision) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new AbiFixed(length, precision));

    [TestCase(0)]
    [TestCase(7)]
    [TestCase(264)]
    public void Test_int_exception(int length) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new AbiInt(length));

    [TestCase(0)]
    [TestCase(7)]
    [TestCase(264)]
    public void Test_uint_exception(int length) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new AbiUInt(length));

    [TestCase("uint64[abc]")]
    [TestCase("bytes32[xyz]")]
    [TestCase("address[!@#]")]
    public void Test_invalid_array_syntax_exception(string type) =>
        Assert.Throws<ArgumentException>(() => System.Text.Json.JsonSerializer.Deserialize<AbiType>($"\"{type}\""));

    [TestCase(AbiEncodingStyle.IncludeSignature)]
    [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.None)]
    public void Test_single_address_no_signature(AbiEncodingStyle encodingStyle)
    {
        AbiType type = AbiType.Address;
        AbiSignature signature = new("abc", type);
        Address arg = new(Keccak.OfAnEmptyString);
        byte[] encoded = _abiEncoder.Encode(AbiEncodingStyle.None, signature, arg);
        object[] arguments = _abiEncoder.Decode(AbiEncodingStyle.None, signature, encoded);
        Assert.That(arguments[0], Is.EqualTo(arg));
    }

    [TestCase(AbiEncodingStyle.IncludeSignature)]
    [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.None)]
    public void Test_packed(AbiEncodingStyle encodingStyle)
    {
        Hash256 assetId = Keccak.Compute("assetId");
        uint expiryTime = (uint)Timestamper.Default.UnixTime.Seconds + 86000;
        UInt256 value = 1.Ether;
        uint units = 10U;
        byte[] salt = new byte[16];

        AbiSignature abiDef = new("example",
            new AbiBytes(32),
            new AbiUInt(32),
            new AbiUInt(96),
            new AbiUInt(32),
            new AbiBytes(16),
            AbiType.Address,
            AbiType.Address);

        byte[] encoded = _abiEncoder.Encode(AbiEncodingStyle.Packed, abiDef, assetId.BytesToArray(), units, value, expiryTime, salt, Address.Zero, Address.Zero);
        Assert.That(encoded.Length, Is.EqualTo(108));
    }

    [TestCase(AbiEncodingStyle.IncludeSignature)]
    [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.None)]
    public void Static_tuple(AbiEncodingStyle encodingStyle)
    {
        AbiType type = new AbiTuple(AbiType.UInt256, AbiType.Address, AbiType.Bool);

        AbiSignature signature = new("abc", type);

        ValueTuple<UInt256, Address, bool> staticTuple = new((UInt256)1000, Address.SystemUser, true);
        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, staticTuple);
        object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(arguments[0], Is.EqualTo(staticTuple));
    }

    [TestCase(AbiEncodingStyle.IncludeSignature)]
    [TestCase(AbiEncodingStyle.None)]
    public void Dynamic_tuple(AbiEncodingStyle encodingStyle)
    {
        AbiType type = new AbiTuple(AbiType.DynamicBytes, AbiType.Address, AbiType.DynamicBytes);

        AbiSignature signature = new("abc", type);

        ValueTuple<byte[], Address, byte[]> dynamicTuple = new(Bytes.FromHexString("0x004749fa3d"), Address.SystemUser, Bytes.Zero32);
        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, dynamicTuple);
        object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(arguments[0], Is.EqualTo(dynamicTuple));
    }


    [TestCase(AbiEncodingStyle.IncludeSignature)]
    [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.None)]
    public void Multiple_params_with_one_of_them_a_tuple(AbiEncodingStyle encodingStyle)
    {
        AbiType type = new AbiTuple(AbiType.UInt256, AbiType.Address, AbiType.Bool);

        AbiSignature signature = new("abc", type, AbiType.String);

        ValueTuple<UInt256, Address, bool> staticTuple = new((UInt256)1000, Address.SystemUser, true);
        const string stringParam = "hello there!";
        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, staticTuple, stringParam);
        object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(arguments[0], Is.EqualTo(staticTuple));
        Assert.That(arguments[1], Is.EqualTo(stringParam));
    }

    [TestCase(AbiEncodingStyle.IncludeSignature)]
    [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.None)]
    public void Multiple_params_with_one_of_them_a_tuple_dynamic_first(AbiEncodingStyle encodingStyle)
    {
        AbiType type = new AbiTuple(AbiType.UInt256, AbiType.Address, AbiType.Bool);

        AbiSignature signature = new("abc", AbiType.String, type);

        ValueTuple<UInt256, Address, bool> staticTuple = new((UInt256)1000, Address.SystemUser, true);
        const string stringParam = "hello there!";
        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, stringParam, staticTuple);
        object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(arguments[0], Is.EqualTo(stringParam));
        Assert.That(arguments[1], Is.EqualTo(staticTuple));
    }

    [TestCase(AbiEncodingStyle.IncludeSignature)]
    [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.None)]
    public void Tuple_with_inner_static_tuple(AbiEncodingStyle encodingStyle)
    {
        AbiType type = new AbiTuple(AbiType.UInt256, new AbiTuple(AbiType.UInt256, AbiType.Address), AbiType.Bool);

        AbiSignature signature = new("abc", type);

        ValueTuple<UInt256, ValueTuple<UInt256, Address>, bool> staticTuple = new((UInt256)1000, new ValueTuple<UInt256, Address>((UInt256)400, Address.SystemUser), true);
        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, staticTuple);
        object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(arguments[0], Is.EqualTo(staticTuple));
    }

    [TestCase(AbiEncodingStyle.IncludeSignature)]
    [TestCase(AbiEncodingStyle.None)]
    public void Tuple_with_inner_dynamic_tuple(AbiEncodingStyle encodingStyle)
    {
        AbiType type = new AbiTuple(AbiType.UInt256, new AbiTuple(AbiType.DynamicBytes, AbiType.Address), AbiType.Bool);

        AbiSignature signature = new("abc", type);

        ValueTuple<UInt256, ValueTuple<byte[], Address>, bool> dynamicTuple = new((UInt256)1000, new ValueTuple<byte[], Address>(Bytes.FromHexString("0x019283fa3d"), Address.SystemUser), true);
        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, dynamicTuple);
        object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(arguments[0], Is.EqualTo(dynamicTuple));
    }


    [TestCase(AbiEncodingStyle.IncludeSignature)]
    [TestCase(AbiEncodingStyle.None)]
    public void Dynamic_tuple_with_inner_dynamic_tuple(AbiEncodingStyle encodingStyle)
    {
        AbiType type = new AbiTuple(AbiType.DynamicBytes, new AbiTuple(AbiType.DynamicBytes, AbiType.Address), AbiType.Bool);

        AbiSignature signature = new("abc", type);

        ValueTuple<byte[], ValueTuple<byte[], Address>, bool> dynamicTuple = new(Bytes.FromHexString("0x019283fa3d"), new ValueTuple<byte[], Address>(Bytes.FromHexString("0x019283fa3d"), Address.SystemUser), true);
        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, dynamicTuple);
        object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(arguments[0], Is.EqualTo(dynamicTuple));
    }

    [TestCase(AbiEncodingStyle.IncludeSignature)]
    [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.None)]
    public void Tuple_with_inner_tuple_with_inner_tuple(AbiEncodingStyle encodingStyle)
    {
        AbiType type = new AbiTuple(new AbiTuple(new AbiTuple(AbiType.UInt256)));

        AbiSignature signature = new("abc", type);

        ValueTuple<ValueTuple<ValueTuple<UInt256>>> nestedTuple = new(new ValueTuple<ValueTuple<UInt256>>(new ValueTuple<UInt256>(88888)));
        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, nestedTuple);
        object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(arguments[0], Is.EqualTo(nestedTuple));
    }

    [Test]
    public void Can_decode_array_of_dynamic_tuples()
    {
        AbiType type = new AbiArray(new AbiTuple<UserOperationAbi>());
        AbiSignature signature = new("handleOps", type, AbiType.Address);

        object[] objects = _abiEncoder.Decode(AbiEncodingStyle.IncludeSignature, signature, Bytes.FromHexString("0x9984521800000000000000000000000000000000000000000000000000000000000000400000000000000000000000004173c8ce71a385e325357d8d79d6b7bc1c708f40000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000200000000000000000000000004ed7c70f96b99c776995fb64377f0d4ab3b0e1c10000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000018000000000000000000000000000000000000000000000000000000000000001a0000000000000000000000000000000000000000000000000000000000001a5b8000000000000000000000000000000000000000000000000000000000007a1200000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000260000000000000000000000000fc7c490fc83e74556aa353ac360cf766e0d4313e000000000000000000000000000000000000000000000000000000000000028000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000084be6002c200000000000000000000000009635f643e140090a9a8dcd712ed6285858cebef0000000000000000000000000000000000000000000000000000000000000040000000000000000000000000000000000000000000000000000000000000000406661abd000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000041c0b5810722f6d3ff73d1e22ec2120670a6ae63ee916c026517a55754e7dd9a7b5d9b6aa5046bb35d009e034aace90845823e8365dbb22c2aa591fb60cd5c40001c00000000000000000000000000000000000000000000000000000000000000"));

        object[] expectedObjects =
        [
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
        ];

        Assert.That(objects, Is.EqualTo(expectedObjects).UsingPropertiesComparer());
    }

    [Test]
    public void Should_encode_arrays_and_lists_equally()
    {
        AbiArray abi = new(AbiType.UInt256);
        UInt256[] array = new UInt256[] { 1, 2, 3, UInt256.MaxValue };
        List<UInt256> list = [1, 2, 3, UInt256.MaxValue];
        using ArrayPoolList<UInt256> pool = new(4);

        pool.AddRange(array);

        byte[] encoded = abi.Encode(array, false);

        Assert.That(abi.Encode(list, false), Is.EqualTo(encoded));
        Assert.That(abi.Encode(pool, false), Is.EqualTo(encoded));
    }

    [Test]
    public void Should_throw_on_malformed_abi()
    {
        AbiSignature abi = new(
            "DepositEvent",
            AbiType.DynamicBytes,
            AbiType.DynamicBytes,
            AbiType.DynamicBytes,
            AbiType.DynamicBytes,
            AbiType.DynamicBytes
        );

        // Malformed ABI: declares length=200 but insufficient data.
        byte[] data = new byte[256];
        data[31] = 160;  // First offset.
        data[191] = 200; // Length = 200 (oversized for available data).

        Assert.Throws<AbiException>(() => new AbiEncoder().Decode(AbiEncodingStyle.None, abi, data));
    }

    [TestCaseSource(nameof(DynamicBytesAllocationCases))]
    public void Should_reject_dynamic_bytes_length_before_allocating(
        AbiEncodingStyle encodingStyle,
        AbiType type,
        UInt256 declaredLength,
        int allocationLimit)
    {
        AbiSignature signature = new("f", type);
        byte[] data = DynamicValueWithMissingPayload(declaredLength);

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<AbiException>(() => _abiEncoder.Decode(encodingStyle, signature, data));
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.That(allocatedBytes, Is.LessThan(allocationLimit));
    }

    [TestCaseSource(nameof(DynamicArrayAllocationCases))]
    public void Should_reject_dynamic_array_length_before_allocating(
        AbiEncodingStyle encodingStyle,
        AbiType elementType,
        UInt256 declaredLength,
        int trailingDataLength,
        int allocationLimit)
    {
        AbiSignature signature = new("f", new AbiArray(elementType));
        byte[] data = DynamicValueWithMissingPayload(declaredLength, trailingDataLength);

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<AbiException>(() => _abiEncoder.Decode(encodingStyle, signature, data));
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.That(allocatedBytes, Is.LessThan(allocationLimit));
    }

    [Test]
    public void Should_bound_cumulative_nested_array_allocations()
    {
        const int outerLength = 128;
        const int innerLength = 128;
        AbiSignature signature = new("f", new AbiArray(new AbiArray(AbiType.UInt256)));
        byte[] data = NestedArrayWithAliasedOffsets(outerLength, innerLength);

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<AbiException>(() => _abiEncoder.Decode(AbiEncodingStyle.None, signature, data));
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.That(allocatedBytes, Is.LessThan(1_000_000));
    }

    [Test]
    public void Should_reject_fixed_array_length_before_allocating()
    {
        AbiFixedLengthArray type = new(AbiType.Bool, 10_000_000);

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<AbiException>(() => type.Decode(new byte[32], 0, false));
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.That(allocatedBytes, Is.LessThan(1_000_000));
    }

    [Test]
    public void Should_wrap_oversized_composite_head()
    {
        AbiSignature signature = new("f", new AbiFixedLengthArray(AbiType.UInt256, int.MaxValue));

        Assert.Throws<AbiException>(() => _abiEncoder.Decode(AbiEncodingStyle.None, signature, []));
    }

    [TestCase(AbiEncodingStyle.None)]
    [TestCase(AbiEncodingStyle.Packed)]
    public void Empty_tuple_roundtrips(AbiEncodingStyle encodingStyle)
    {
        AbiSignature signature = new("f", new AbiTuple());
        ValueTuple value = new();

        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, value);
        object[] decoded = _abiEncoder.Decode(encodingStyle, signature, encoded);

        Assert.That(decoded[0], Is.EqualTo(value));
    }

    [TestCase(AbiEncodingStyle.None, false, 16_384)]
    [TestCase(AbiEncodingStyle.None, true, 2)]
    [TestCase(AbiEncodingStyle.Packed, false, 65)]
    [TestCase(AbiEncodingStyle.Packed, true, 2)]
    public void Empty_tuple_array_roundtrips(AbiEncodingStyle encodingStyle, bool fixedLength, int length)
    {
        AbiTuple elementType = new();
        AbiType arrayType = fixedLength ? new AbiFixedLengthArray(elementType, length) : new AbiArray(elementType);
        ValueTuple[] value = new ValueTuple[length];
        AbiSignature signature = new("f", arrayType);

        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, value);
        object[] decoded = _abiEncoder.Decode(encodingStyle, signature, encoded);

        Assert.That(decoded[0], Is.EqualTo(value));
    }

    [TestCase(AbiEncodingStyle.None)]
    [TestCase(AbiEncodingStyle.IncludeSignature)]
    public void Should_wrap_out_of_range_decode_error(AbiEncodingStyle encodingStyle)
    {
        AbiSignature signature = new("f", AbiType.Bool);

        Assert.Throws<AbiException>(() => _abiEncoder.Decode(encodingStyle, signature, []));
    }

    [Test]
    public void Should_reject_dynamic_bytes_length_one_exceeding_data()
    {
        // Length 1 is the case that would reach `ByteArrayExtensions.Slice`'s one-byte fast path and
        // surface as `IndexOutOfRangeException` if the bounds check were ever dropped.
        AbiSignature signature = new("f", AbiType.DynamicBytes);

        Assert.Throws<AbiException>(() => _abiEncoder.Decode(AbiEncodingStyle.None, signature, DynamicValueWithMissingPayload(1)));
    }

    private static byte[] DynamicValueWithMissingPayload(UInt256 declaredLength, int trailingDataLength = 0)
    {
        byte[] data = new byte[64 + trailingDataLength];
        AbiType.UInt256.Encode(32, false).CopyTo(data, 0);
        AbiType.UInt256.Encode(declaredLength, false).CopyTo(data, 32);
        return data;
    }

    private static byte[] NestedArrayWithAliasedOffsets(int outerLength, int innerLength)
    {
        int innerDataPosition = 64 + outerLength * 32;
        byte[] data = new byte[innerDataPosition + 32 + innerLength * 32];
        AbiType.UInt256.Encode(32, false).CopyTo(data, 0);
        AbiType.UInt256.Encode(outerLength, false).CopyTo(data, 32);
        byte[] innerOffset = AbiType.UInt256.Encode(outerLength * 32, false);
        for (int i = 0; i < outerLength; i++)
        {
            innerOffset.CopyTo(data, 64 + i * 32);
        }

        AbiType.UInt256.Encode(innerLength, false).CopyTo(data, innerDataPosition);
        return data;
    }

    private static IEnumerable<TestCaseData> DynamicBytesAllocationCases()
    {
        yield return new TestCaseData(AbiEncodingStyle.None, AbiType.DynamicBytes, (UInt256)1_000_000, 1_000_000)
            .SetName("Should_reject_dynamic_bytes_length_before_allocating_standard");
        yield return new TestCaseData(AbiEncodingStyle.Packed, AbiType.DynamicBytes, (UInt256)1_000_000, 1_000_000)
            .SetName("Should_reject_dynamic_bytes_length_before_allocating_packed");
        yield return new TestCaseData(AbiEncodingStyle.None, AbiType.String, (UInt256)1_000_000, 1_000_000)
            .SetName("Should_reject_dynamic_string_length_before_allocating_standard");
        yield return new TestCaseData(AbiEncodingStyle.Packed, AbiType.String, (UInt256)1_000_000, 1_000_000)
            .SetName("Should_reject_dynamic_string_length_before_allocating_packed");
        yield return new TestCaseData(AbiEncodingStyle.None, AbiType.DynamicBytes, (UInt256)int.MaxValue, 1_000_000)
            .SetName("Should_reject_dynamic_bytes_max_int_length_before_allocating");
        yield return new TestCaseData(AbiEncodingStyle.None, AbiType.DynamicBytes, UInt256.MaxValue, 1_000_000)
            .SetName("Should_reject_dynamic_bytes_uint256_max_length_before_allocating");
    }

    private static IEnumerable<TestCaseData> DynamicArrayAllocationCases()
    {
        yield return new TestCaseData(AbiEncodingStyle.None, AbiType.Bool, (UInt256)1_000_000, 0, 1_000_000)
            .SetName("Should_reject_dynamic_array_length_before_allocating_standard_bool");
        yield return new TestCaseData(AbiEncodingStyle.None, new AbiFixedLengthArray(AbiType.UInt256, 2), (UInt256)10_000, 320_000, 10_000)
            .SetName("Should_reject_dynamic_array_length_before_allocating_standard_composite");
        yield return new TestCaseData(AbiEncodingStyle.Packed, AbiType.UInt256, (UInt256)100_000, 100_000, 100_000)
            .SetName("Should_reject_dynamic_array_length_before_allocating_packed_uint256");
        yield return new TestCaseData(AbiEncodingStyle.Packed, new AbiFixed(8, 1), (UInt256)100_000, 100_000, 100_000)
            .SetName("Should_reject_dynamic_array_length_before_allocating_packed_fixed");
        yield return new TestCaseData(AbiEncodingStyle.Packed, new AbiUFixed(8, 1), (UInt256)100_000, 100_000, 100_000)
            .SetName("Should_reject_dynamic_array_length_before_allocating_packed_ufixed");
        yield return new TestCaseData(AbiEncodingStyle.None, new AbiTuple(), (UInt256)16_385, 0, 1_000_000)
            .SetName("Should_reject_dynamic_array_length_before_allocating_empty_tuple");
        yield return new TestCaseData(
                AbiEncodingStyle.None,
                new AbiFixedLengthArray(new AbiTuple(), 16_384),
                (UInt256)16_384,
                0,
                1_000_000)
            .SetName("Should_bound_nested_zero_width_array_allocations");
        yield return new TestCaseData(AbiEncodingStyle.None, AbiType.Bool, (UInt256)int.MaxValue, 0, 1_000_000)
            .SetName("Should_reject_dynamic_array_length_before_allocating_max_length");
        yield return new TestCaseData(AbiEncodingStyle.None, AbiType.Bool, UInt256.MaxValue, 0, 1_000_000)
            .SetName("Should_reject_dynamic_array_length_before_allocating_uint256_max_length");
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

    [TestCase(AbiEncodingStyle.IncludeSignature)]
    [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.None)]
    public void Dynamic_array_of_fixed_array_of_uint64(AbiEncodingStyle encodingStyle)
    {
        AbiType type = new AbiArray(new AbiFixedLengthArray(new AbiUInt(64), 3));
        ulong[] element = [100UL, 200UL, 300UL];
        ulong[][] data = [element, [400UL, 500UL, 600UL]];
        AbiSignature signature = new("abc", type);
        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, [data]);
        object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(arguments[0], Is.EqualTo(data));
    }

    [TestCase(AbiEncodingStyle.IncludeSignature)]
    [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.None)]
    public void Dynamic_array_of_fixed_array_of_uint64_single_element(AbiEncodingStyle encodingStyle)
    {
        AbiType type = new AbiArray(new AbiFixedLengthArray(new AbiUInt(64), 3));
        ulong[][] data = [[1000000UL, 7UL, 3600UL]];
        AbiSignature signature = new("abc", type);
        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, [data]);
        object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(arguments[0], Is.EqualTo(data));
    }

    [TestCase(AbiEncodingStyle.IncludeSignature)]
    [TestCase(AbiEncodingStyle.IncludeSignature | AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.Packed)]
    [TestCase(AbiEncodingStyle.None)]
    public void Dynamic_array_of_fixed_array_of_uint64_empty(AbiEncodingStyle encodingStyle)
    {
        AbiType type = new AbiArray(new AbiFixedLengthArray(new AbiUInt(64), 3));
        ulong[][] data = [];
        AbiSignature signature = new("abc", type);
        byte[] encoded = _abiEncoder.Encode(encodingStyle, signature, [data]);
        object[] arguments = _abiEncoder.Decode(encodingStyle, signature, encoded);
        Assert.That(arguments[0], Is.EqualTo(data));
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

        AbiSignature signature = new(
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
        Assert.That(encoded.ToHexString(), Is.EqualTo(expectedValue.ToHexString()));
    }

    [TestCase("tuple", typeof(AbiTuple), "()")]
    [TestCase("tuple[]", typeof(AbiArray), "()[]")]
    [TestCase("tuple[3]", typeof(AbiFixedLengthArray), "()[3]")]
    [TestCase("tuple[][]", typeof(AbiArray), "()[][]")]
    [TestCase("tuple[2][]", typeof(AbiArray), "()[2][]")]
    public void AbiTypeConverter_Parses_Tuple_Variants(string typeName, Type expectedType, string expectedName)
    {
        AbiType result = JsonSerializer.Deserialize<AbiType>($"\"{typeName}\"")!;

        Assert.That(result, Is.TypeOf(expectedType));
        Assert.That(result.Name, Is.EqualTo(expectedName));
    }

    [Test]
    public void AbiTuple_Name_Reflects_Elements()
    {
        AbiTuple tuple = new(AbiType.UInt8, AbiType.UInt64);
        Assert.That(tuple.Name, Is.EqualTo("(uint8,uint64)"));

        AbiArray tupleArray = new(tuple);
        Assert.That(tupleArray.Name, Is.EqualTo("(uint8,uint64)[]"));
    }
}
