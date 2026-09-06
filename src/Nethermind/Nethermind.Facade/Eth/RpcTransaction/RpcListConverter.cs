// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Facade.Eth.RpcTransaction;

/// <summary>Element-wise mapping of the deserialized JSON list views onto their transaction types.</summary>
internal static class RpcListConverter
{
    /// <summary>Maps <paramref name="views"/> element-wise through <paramref name="convert"/>.</summary>
    /// <remarks>
    /// A JSON <c>null</c> element deserializes to a null reference that none of the element types accept, so
    /// it is reported rather than dereferenced.
    /// </remarks>
    /// <param name="views">The deserialized list, or <c>null</c> when the request omitted it.</param>
    /// <param name="convert">The per-element mapping.</param>
    /// <param name="converted">The mapped list, or <c>null</c> when <paramref name="views"/> is absent.</param>
    /// <returns><c>false</c> if any element was JSON <c>null</c>.</returns>
    public static bool TryConvert<TView, TValue>(TView[]? views, Func<TView, TValue> convert, out TValue[]? converted)
        where TView : class
    {
        converted = null;
        if (views is null) return true;

        TValue[] result = new TValue[views.Length];
        for (int i = 0; i < views.Length; i++)
        {
            if (views[i] is null) return false;
            result[i] = convert(views[i]);
        }

        converted = result;
        return true;
    }
}
