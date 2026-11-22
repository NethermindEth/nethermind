// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing.GethStyle.Custom.JavaScript;

public static class JavaScriptConverter
{
    public static byte[] ToBytes(this object input) => input switch
    {
        string hexString => Bytes.FromHexString(hexString),
        ITypedArray<byte> typedArray => typedArray.Length == 0 ? [] : typedArray.ToArray(),
        IArrayBuffer arrayBuffer => arrayBuffer.GetBytes(),
        IArrayBufferView arrayBufferView => arrayBufferView.GetBytes(),
        ScriptObject values and IList => CurrentEngine.ToBytes(values),
        IList list => ToWrappedBytes(list),
        _ => throw new ArgumentException("Unsupported input type.", nameof(input))
    };

    private static byte[] ToWrappedBytes(IList list)
    {
        byte[] bytes = new byte[list.Count];
        for (int i = 0; i < bytes.Length; i++)
        {
            double value = list[i] is IConvertible convertible ? convertible.ToDouble(CultureInfo.InvariantCulture) : 0;
            bytes[i] = double.IsFinite(value) ? unchecked((byte)(long)value) : (byte)0;
        }
        return bytes;
    }

    public static byte[] ToWord(this object input) => input switch
    {
        string hexString => Bytes.FromHexString(hexString, EvmPooledMemory.WordSize),
        _ => ListToWord(input)
    };

    private static byte[] ListToWord(object input) => input.ToBytes().PadLeft(EvmPooledMemory.WordSize);

    public static Address ToAddress(this object address) => address switch
    {
        string hexString => Address.TryParseVariableLength(hexString, out Address parsedAddress, true)
            ? parsedAddress
            : throw new ArgumentException("Not correct address", nameof(address)),
        _ => BytesToAddress(address.ToBytes())
    } ?? throw new ArgumentException("Not correct address", nameof(address));

    private static Address BytesToAddress(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= Address.Size)
            return new Address(bytes[^Address.Size..]);

        Span<byte> buffer = stackalloc byte[Address.Size];
        buffer.Clear();
        bytes.CopyTo(buffer[^bytes.Length..]);
        return new Address(buffer);
    }

    private static Engine CurrentEngine => Engine.CurrentEngine ?? throw new InvalidOperationException("No engine set");

    public static ITypedArray<byte> ToTypedScriptArray(this byte[] array) => CurrentEngine.CreateUint8Array(array);

    public static ITypedArray<byte> ToTypedScriptArray(this ReadOnlySpan<byte> span) => CurrentEngine.CreateUint8Array(span);

    public static ITypedArray<byte> ToTypedScriptArray(this ReadOnlyMemory<byte> memory) => CurrentEngine.CreateUint8Array(memory.Span);

    public static IJavaScriptObject ToBigInteger(this BigInteger bigInteger) => CurrentEngine.CreateBigInteger(bigInteger);
    public static IJavaScriptObject ToBigInteger(this UInt256 bigInteger) => CurrentEngine.CreateBigInteger((BigInteger)bigInteger);



}
