// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;

namespace Nethermind.Runner.JsonRpc;

internal static class RestSszEncoder
{
    private const string EncoderTypeName = "Nethermind.Serialization.SszEncoding";
    private static readonly ConcurrentDictionary<Type, MethodInfo?> _encodeCache = new();

    public static bool TryEncode(object? data, Type? declaredType, out byte[]? encoded)
    {
        if (data is null)
        {
            encoded = Array.Empty<byte>();
            return true;
        }

        Type targetType = declaredType ?? data.GetType();
        MethodInfo? encodeMethod = _encodeCache.GetOrAdd(targetType, FindEncodeMethod);
        if (encodeMethod is null)
        {
            encoded = null;
            return false;
        }

        encoded = (byte[]?)encodeMethod.Invoke(null, [data]);
        return encoded is not null;
    }

    private static MethodInfo? FindEncodeMethod(Type dataType)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? encoderType = assembly.GetType(EncoderTypeName, throwOnError: false);
            if (encoderType is null)
            {
                continue;
            }

            MethodInfo? method = encoderType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m =>
                    m.Name == "Encode" &&
                    m.ReturnType == typeof(byte[]) &&
                    m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType.IsAssignableFrom(dataType));

            if (method is not null)
            {
                return method;
            }
        }

        return null;
    }
}
