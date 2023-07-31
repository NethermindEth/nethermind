// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.IO;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc;

public static class JsonSerializerExtensions
{
    public static long SerializeWaitForEnumeration<T>(this IJsonSerializer serializer, Stream stream, T value, bool indented = false)
    {
        AmendValue(value);
        return serializer.Serialize(stream, value, indented);
    }

    public static string SerializeWaitForEnumeration<T>(this IJsonSerializer serializer, T value, bool indented = false)
    {
        AmendValue(value);
        return serializer.Serialize(value, indented);
    }

    private static void AmendValue<T>(T value)
    {
        static IEnumerable NewEnumerable(IEnumerator enumerator)
        {
            yield return enumerator.Current;
            while (enumerator.MoveNext())
            {
                yield return enumerator.Current;
            }
        }

        if (value is JsonRpcSuccessResponse { Result: IEnumerable enumerable } response)
        {
            IEnumerator enumerator = enumerable.GetEnumerator();
            if (enumerator.MoveNext())
            {
                response.Result = NewEnumerable(enumerator);
            }
        }
    }
}
