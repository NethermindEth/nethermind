// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Nethermind.JsonRpc;

/// <summary>Registers generated JSON-RPC method names that can be reused while parsing request envelopes.</summary>
public static class RpcKnownMethodNamesRegistry
{
    /// <summary>Registers method names discovered from an RPC module assembly.</summary>
    public static void Register(string[] methodNames) => KnownRpcMethodNames.Register(methodNames);
}

internal static class KnownRpcMethodNames
{
    private static readonly Lock _lock = new();
    private static Dictionary<int, MethodName[]> _methodNamesByLength = [];
    private static string[] _all = [];

    public static IReadOnlyList<string> All => Volatile.Read(ref _all);

    public static string? Intern(ref Utf8JsonReader methodReader)
    {
        int methodLength = methodReader.HasValueSequence
            ? checked((int)methodReader.ValueSequence.Length)
            : methodReader.ValueSpan.Length;

        Dictionary<int, MethodName[]> methodNamesByLength = Volatile.Read(ref _methodNamesByLength);
        if (methodNamesByLength.TryGetValue(methodLength, out MethodName[]? methodNames))
        {
            for (int i = 0; i < methodNames.Length; i++)
            {
                MethodName methodName = methodNames[i];
                if (methodReader.ValueTextEquals(methodName.Utf8))
                {
                    return methodName.Name;
                }
            }
        }

        return methodReader.GetString();
    }

    public static string? Intern(JsonElement methodElement)
    {
        string[] methods = Volatile.Read(ref _all);
        for (int i = 0; i < methods.Length; i++)
        {
            string methodName = methods[i];
            if (methodElement.ValueEquals(methodName))
            {
                return methodName;
            }
        }

        return methodElement.GetString();
    }

    internal static void Register(string[] methodNames)
    {
        if (methodNames.Length == 0)
        {
            return;
        }

        lock (_lock)
        {
            string[] all = Volatile.Read(ref _all);
            HashSet<string> names = new(all, StringComparer.Ordinal);
            names.UnionWith(methodNames);
            string[] updated = new string[names.Count];
            names.CopyTo(updated);
            Array.Sort(updated, StringComparer.Ordinal);

            Dictionary<int, List<MethodName>> buildersByLength = [];
            for (int i = 0; i < updated.Length; i++)
            {
                string methodName = updated[i];
                int length = Encoding.UTF8.GetByteCount(methodName);
                if (!buildersByLength.TryGetValue(length, out List<MethodName>? methodNamesByLength))
                {
                    methodNamesByLength = [];
                    buildersByLength.Add(length, methodNamesByLength);
                }

                methodNamesByLength.Add(new MethodName(methodName));
            }

            Dictionary<int, MethodName[]> updatedByLength = new(buildersByLength.Count);
            foreach (KeyValuePair<int, List<MethodName>> pair in buildersByLength)
            {
                updatedByLength.Add(pair.Key, [.. pair.Value]);
            }

            Volatile.Write(ref _methodNamesByLength, updatedByLength);
            Volatile.Write(ref _all, updated);
        }
    }

    private readonly record struct MethodName(string Name)
    {
        public byte[] Utf8 { get; } = Encoding.UTF8.GetBytes(Name);
    }
}
