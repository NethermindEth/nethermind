// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using System.Runtime.InteropServices;

namespace Nethermind.Evm.Tracing.GethStyle.Javascript;

public static class JavascriptConverter
{
    public static string ToHexString(this IList list)
    {
        using ArrayPoolList<byte>? pooledList = new(list.Count, list.ToBytes());
        string hexString = pooledList.AsSpan().ToHexString();
        return "0x" + hexString;
    }

    // Slice
    public static byte[] Slice(this IList input, int startIndex, int endIndex)
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

        int length = endIndex - startIndex;
        return input.ToBytes().Skip(startIndex).Take(length).ToArray();
    }

    public static string ToHexAddress(this IList list)
    {
        Span<byte> address = stackalloc byte[20];
        for (int i = 0; i < 20; i++)
        {
            address[i] = (byte)list[i];
        }

        return address.ToHexString();
    }

    public static IEnumerable<byte> ToBytes(this IList list) => list.ToEnumerable().Select(Convert.ToByte);

    public static byte[]? ToWord(this object input) => input switch
    {
        string hexString => Bytes.FromHexString(hexString, EvmPooledMemory.WordSize),
        IList list => list.ToBytes()
            .Concat(Enumerable.Repeat((byte)0, Math.Max(0, EvmPooledMemory.WordSize - list.Count)))
            .Take(EvmPooledMemory.WordSize).ToArray(),
        _ => null
    };

    public static byte[]? ToBytes(this object input) => input switch
    {
        string hexString => Bytes.FromHexString(hexString),
        IList list => list.ToBytes().ToArray(),
        _ => null
    };

    public static Address ToAddress(this IList address) => new(address.ToBytes().ToArray());

    public static Address ToAddress(this object address) => address switch
    {
        string hexString => Address.TryParseVariableLength(hexString, out Address parsedAddress)
            ? parsedAddress
            : throw new ArgumentException("Not correct address", nameof(address)),
        IList list => list.ToAddress(),
        _ => throw new ArgumentException("Not correct address", nameof(address))
    } ?? throw new ArgumentException("Not correct address", nameof(address));

    public static UInt256 GetUint256(this IList index)
    {
        Span<byte> indexSpan = stackalloc byte[32];
        for (int i = 0; i < index.Count; i++)
        {
            indexSpan[i] = (byte)index[i];
        }

        return new UInt256(indexSpan);
    }

    public static dynamic ToScriptArray(this Array array, ScriptEngine engine) => engine.Script.Array.from(array);
}
