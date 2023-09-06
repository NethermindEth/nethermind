// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.JsonRpc.Test.Sockets;

public class RandomString
{
    private const string Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private readonly string _string;

    public RandomString(int length, Random? random = null)
    {
        char[] stringChars = new char[length];
        random ??= new Random();

        for (int i = 0; i < stringChars.Length; i++)
        {
            stringChars[i] = Chars[random.Next(Chars.Length)];
        }

        _string = new string(stringChars);
    }

    public override string ToString()
    {
        return _string;
    }
}
