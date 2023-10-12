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
    // Precompiles bool
    public static bool IsPrecompile(this IList list)
    {
        Span<uint> data = MemoryMarshal.Cast<byte, uint>(list.ToBytes().ToArray());
        return (data[4] & 0x00ffffff) == 0
               && data[3] == 0 && data[2] == 0 && data[1] == 0 && data[0] == 0
               && (data[4] >>> 24) switch
               {
                   0x01 => true,
                   0x02 => true,
                   0x03 => true,
                   0x04 => true,
                   0x05 => true,
                   0x06 => true,
                   0x07 => true,
                   0x08 => true,
                   0x09 => true,
                   0x0a => true,
                   0x0c => true,
                   0x0d => true,
                   0x0e => true,
                   0x0f => true,
                   0x10 => true,
                   0x11 => true,
                   0x12 => true,
                   0x13 => true,
                   0x14 => true,
                   _ => false
               };
    }
    // Slice
    public static string SliceAndConvertToString(string sourceString, int startIndex, int endIndex)
    {
        if (sourceString == null)
        {
            throw new ArgumentNullException(nameof(sourceString));
        }

        if (startIndex < 0 || startIndex >= sourceString.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(startIndex), "Start index is out of range.");
        }

        if (endIndex <= startIndex || endIndex > sourceString.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(endIndex), "End index is out of range.");
        }

        int length = endIndex - startIndex;
        string slicedString = sourceString.Substring(startIndex, length);
        return slicedString;
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

    public static Address GetAddress(this IList address) => new(address.ToBytes().ToArray());

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
