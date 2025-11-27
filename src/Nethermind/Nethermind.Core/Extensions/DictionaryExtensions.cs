// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Resettables;

namespace Nethermind.Core.Extensions;

public static class DictionaryExtensions
{
    public static void ResetAndClear<TKey, TValue>(this IDictionary<TKey, TValue> dictionary)
        where TValue : class, IReturnable
    {
        foreach (TValue value in dictionary.Values)
        {
            value.Return();
        }
        dictionary.Clear();
    }
}
