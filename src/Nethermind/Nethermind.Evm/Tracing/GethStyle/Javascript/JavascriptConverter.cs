// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.ClearScript.JavaScript;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing.GethStyle.Javascript;

public static class JavascriptConverter
{
    public static byte[] ToBytes(this object input) => input switch
    {
        string hexString => Bytes.FromHexString(hexString),
        ITypedArray<byte> typedArray => typedArray.Length == 0 ? Array.Empty<byte>() : typedArray.ToArray(),
        IArrayBuffer arrayBuffer => arrayBuffer.GetBytes(),
        IArrayBufferView arrayBufferView => arrayBufferView.GetBytes(),
        IList list => list.ToEnumerable().Select(Convert.ToByte).ToArray(),
        _ => throw new ArgumentException(nameof(input))
    };

    public static byte[] ToWord(this object input) => input switch
    {
        string hexString => Bytes.FromHexString(hexString, EvmPooledMemory.WordSize),
        _ => ListToWord(input)
    };

    private static byte[] ListToWord(object input)
    {
        byte[] bytes = input.ToBytes();
        return bytes.Length == EvmPooledMemory.WordSize
            ? bytes
            : bytes
                .Concat(Enumerable.Repeat((byte)0, Math.Max(0, EvmPooledMemory.WordSize - bytes.Length)))
                .Take(EvmPooledMemory.WordSize).ToArray();
    }

    public static Address ToAddress(this object address) => address switch
    {
        string hexString => Address.TryParseVariableLength(hexString, out Address parsedAddress)
            ? parsedAddress
            : throw new ArgumentException("Not correct address", nameof(address)),
        _ => new Address(address.ToBytes())
    } ?? throw new ArgumentException("Not correct address", nameof(address));

    public static UInt256 GetUint256(this object index) => new(index.ToBytes());

    private static Engine CurrentEngine => Engine.CurrentEngine ?? throw new InvalidOperationException("No engine set");

    public static ITypedArray<byte> ToTypedScriptArray(this byte[] array) => CurrentEngine.CreateUint8Array(array);

    public static ITypedArray<byte> ToTypedScriptArray(this ReadOnlyMemory<byte> memory) => memory.ToArray().ToTypedScriptArray();

    public static IList ToUnTypedScriptArray(this byte[] array) => CurrentEngine.CreateUntypedArray(array);

    public static dynamic ToBigInteger(this BigInteger bigInteger) => CurrentEngine.CreateBigInteger(bigInteger);
    public static dynamic ToBigInteger(this UInt256 bigInteger) => CurrentEngine.CreateBigInteger((BigInteger)bigInteger);



}
