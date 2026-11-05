// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Collections.Generic;
using Nethermind.Facade.Filters;

namespace Nethermind.Facade.Find;

/// <summary>The logs a <see cref="LogFinder"/> matched, which can also be serialized without materializing them.</summary>
public sealed class FoundLogs : IEnumerable<FilterLog>
{
    private readonly IEnumerable<FilterLog> _logs;
    private readonly IEnumerable<SerializedLogs> _serializedLogs;

    internal FoundLogs(IEnumerable<FilterLog> logs, IEnumerable<SerializedLogs> serializedLogs)
    {
        _logs = logs;
        _serializedLogs = serializedLogs;
    }

    public IEnumerator<FilterLog> GetEnumerator() => _logs.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Lazily serializes <paramref name="logs"/> in order, a block at a time when they are <see cref="FoundLogs"/>.</summary>
    /// <remarks>The caller owns, and must dispose, every yielded <see cref="SerializedLogs"/>.</remarks>
    public static IEnumerable<SerializedLogs> Serialize(IEnumerable<FilterLog> logs) =>
        logs is FoundLogs found ? found._serializedLogs : SerializeEach(logs);

    private static IEnumerable<SerializedLogs> SerializeEach(IEnumerable<FilterLog> logs)
    {
        foreach (FilterLog log in logs)
        {
            SerializedLogs serialized = new();
            serialized.Add(log);
            yield return serialized;
        }
    }
}
