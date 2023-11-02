// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing.GethStyle.Javascript;

public static class JavascriptConverter
{
    public static string ToHexString(this IList? list) =>
        list is null ? "0x" : list.ToBytes().ToHexString(true);

    // Slice
    public static byte[] Slice(this IList input, long startIndex, long endIndex)
    {
        if (input == null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (startIndex < 0 || startIndex >= input.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(startIndex), "Start index is out of range.");
        }

        if (endIndex <= startIndex || endIndex > input.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(endIndex), "End index is out of range.");
        }

        int length = (int)(endIndex - startIndex);
        return input.ToBytes().Skip((int)startIndex).Take(length).ToArray();
    }

    public static byte[] ToBytes(this object input) => input switch
    {
        string hexString => Bytes.FromHexString(hexString),
        ITypedArray<byte> typedArray => typedArray.ToArray(),
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
        _ => throw new ArgumentException("Not correct address", nameof(address))
    } ?? throw new ArgumentException("Not correct address", nameof(address));

    [SkipLocalsInit]
    public static UInt256 GetUint256(this IList index)
    {
        Span<byte> indexSpan = stackalloc byte[32];
        for (int i = 0; i < index.Count; i++)
        {
            indexSpan[i] = (byte)(int)index[i];
        }

        return new UInt256(indexSpan);
    }

    public static ScriptObject ToScriptArray(this byte[] array)
        => CurrentEngine?.Script.Array.from(array) ?? throw new InvalidOperationException("No engine set");

    [field: ThreadStatic]
    public static V8ScriptEngine? CurrentEngine { get; set; }
}
