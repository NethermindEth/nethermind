// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;
using System.Text.Json;
using Nethermind.Blockchain.Contracts.Json;

namespace Nethermind.Abi
{
    [JsonConverter(typeof(AbiTypeConverter))]
    public abstract partial class AbiType
    {
        public static AbiDynamicBytes DynamicBytes => AbiDynamicBytes.Instance;
        public static AbiBytes Bytes32 => AbiBytes.Bytes32;
        public static AbiAddress Address => AbiAddress.Instance;
        public static AbiFunction Function => AbiFunction.Instance;
        public static AbiBool Bool => AbiBool.Instance;
        public static AbiInt Int8 => AbiInt.Int8;
        public static AbiInt Int16 => AbiInt.Int16;
        public static AbiInt Int32 => AbiInt.Int32;
        public static AbiInt Int64 => AbiInt.Int64;
        public static AbiInt Int96 => AbiInt.Int96;
        public static AbiInt Int256 => AbiInt.Int256;
        public static AbiUInt UInt8 => AbiUInt.UInt8;
        public static AbiUInt UInt16 => AbiUInt.UInt16;
        public static AbiUInt UInt32 => AbiUInt.UInt32;
        public static AbiUInt UInt64 => AbiUInt.UInt64;
        public static AbiUInt UInt96 => AbiUInt.UInt96;
        public static AbiUInt UInt256 => AbiUInt.UInt256;
        public static AbiString String => AbiString.Instance;
        public static AbiFixed Fixed { get; } = new(128, 18);
        public static AbiUFixed UFixed { get; } = new(128, 18);

        public virtual bool IsDynamic => false;

        public abstract string Name { get; }

        public abstract (object, int) Decode(byte[] data, int position, bool packed);

        public abstract byte[] Encode(object? arg, bool packed);

        public override string ToString() => Name;

        public override int GetHashCode() => Name.GetHashCode();

        public override bool Equals(object? obj) => obj is AbiType type && Name == type.Name;

        protected string AbiEncodingExceptionMessage => $"Argument cannot be encoded by {GetType().Name}";

        public abstract Type CSharpType { get; }
    }
}

namespace Nethermind.Blockchain.Contracts.Json
{
    using Nethermind.Abi;
    
    [JsonDerivedType(typeof(AbiDynamicBytes))]
    [JsonDerivedType(typeof(AbiBytes))]
    [JsonDerivedType(typeof(AbiAddress))]
    [JsonDerivedType(typeof(AbiFunction))]
    [JsonDerivedType(typeof(AbiBool))]
    [JsonDerivedType(typeof(AbiInt))]
    [JsonDerivedType(typeof(AbiUInt))]
    [JsonDerivedType(typeof(AbiString))]
    [JsonDerivedType(typeof(AbiFixed))]
    [JsonDerivedType(typeof(AbiUFixed))]
    public class AbiTypeConverter : JsonConverter<AbiType>
    {
        public override AbiType? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? type = reader.GetString()!;
            return ParseAbiType(type);

            static AbiType ParseAbiType(string type)
            {
                bool isArray = false;
                if (type.EndsWith("[]"))
                {
                    isArray = true;
                    type = type[..^2];
                }

                if (type == "tuple")
                {
                    return new AbiTuple();
                }
                if (type.StartsWith('(') && type.EndsWith(')'))
                {
                    string[] types = type[1..^1].Split(',');
                    return ParseTuple(types);
                }
                if (type.StartsWith("tuple"))
                {
                    string[] types = type.Trim()[6..^1].Split(',');
                    return ParseTuple(types);
                }

                AbiType value = GetType(type);

                return isArray ? new AbiArray(value) : value;

            }

            static AbiType ParseTuple(string[] types)
            {
                AbiType[] abiTypes = new AbiType[types.Length];
                for (int i = 0; i < types.Length; i++)
                {
                    abiTypes[i] = ParseAbiType(types[i].Trim());
                }

                return new AbiTuple(abiTypes);
            }

            static AbiType ParseFixed(string type)
            {
                (int length, int precision) = Parse(type.AsSpan(5));
                return new AbiFixed(length, precision);
            }

            static AbiType ParseUFixed(string type)
            {
                (int length, int precision) = Parse(type.AsSpan(6));
                return new AbiUFixed(length, precision);
            }

            static (int length, int precision) Parse(ReadOnlySpan<char> chars)
            {
                int xPos = chars.IndexOf('x');
                if (xPos == -1)
                {
                    return (-1, -1);
                }

                int length = int.Parse(chars.Slice(0, xPos));
                int precision = int.Parse(chars.Slice(xPos + 1));

                return (length, precision);
            }

            static AbiType GetType(string? type)
            {
                return type switch
                {
                    "address" => AbiType.Address,
                    "function" => AbiType.Function,
                    "bool" => AbiType.Bool,
                    "int8" => AbiType.Int8,
                    "int16" => AbiType.Int16,
                    "int32" => AbiType.Int32,
                    "int64" => AbiType.Int64,
                    "int96" => AbiType.Int96,
                    "int256" => AbiType.Int256,
                    { } when type.StartsWith("int") => new AbiInt(int.Parse(type.AsSpan(3))),
                    "uint8" => AbiType.UInt8,
                    "uint16" => AbiType.UInt16,
                    "uint32" => AbiType.UInt32,
                    "uint64" => AbiType.UInt64,
                    "uint96" => AbiType.UInt96,
                    "uint256" => AbiType.UInt256,
                    { } when type.StartsWith("uint") => new AbiUInt(int.Parse(type.AsSpan(4))),
                    "string" => AbiType.String,
                    "bytes" => AbiType.DynamicBytes,
                    "bytes32" => AbiType.Bytes32,
                    { } when type.StartsWith("bytes") => new AbiBytes(int.Parse(type.AsSpan(5))),
                    "fixed128x18" => AbiType.Fixed,
                    "ufixed128x18" => AbiType.UFixed,
                    { } when type.StartsWith("fixed") => ParseFixed(type),
                    { } when type.StartsWith("ufixed") => ParseUFixed(type),
                    _ => throw new NotSupportedException($"ABI type {type} is not supported.")
                };
            }
        }

        public override void Write(Utf8JsonWriter writer, AbiType value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.Name);
        }
    }
}
