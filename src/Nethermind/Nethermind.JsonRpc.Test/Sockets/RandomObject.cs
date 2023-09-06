// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Evm.Tracing.GethStyle;

namespace Nethermind.JsonRpc.Test.Sockets;

public class RandomObject
{
    private readonly object _object;

    public RandomObject(int size, Random? random = null)
    {
        string[] strings = new RandomStringArray(size / 2, random).ToArray();
        _object = new GethLikeTxTrace()
        {
            Entries =
            {
                new GethTxTraceEntry
                {
                    Stack = strings, Memory = strings,
                }
            }
        };
    }

    public object Get() => _object;
}
