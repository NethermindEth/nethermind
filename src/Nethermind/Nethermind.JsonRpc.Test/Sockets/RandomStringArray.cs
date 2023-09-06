// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.JsonRpc.Test.Sockets;

public class RandomStringArray
{
    private readonly string[] _array;

    public RandomStringArray(int length, Random? random = null, bool runGc = true)
    {
        _array = new string[length];
        for (int i = 0; i < length; i++)
        {
            _array[i] = new RandomString(length, random).ToString();
            if (runGc && i % 100 == 0)
            {
                GC.Collect();
            }
        }
    }

    public string[] ToArray() => _array;
}
