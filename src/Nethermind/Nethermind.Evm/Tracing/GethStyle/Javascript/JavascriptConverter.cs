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

namespace Nethermind.Evm.Tracing.GethStyle.Javascript;

public static class JavascriptConverter
{
    public static string ToHexString(this IList list)
    {
        using ArrayPoolList<byte>? pooledList = new(list.Count, list.ToBytes());
        string hexString = pooledList.AsSpan().ToHexString();
        return hexString;
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
