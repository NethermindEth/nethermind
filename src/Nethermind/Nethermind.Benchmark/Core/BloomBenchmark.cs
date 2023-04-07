// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

using BenchmarkDotNet.Attributes;

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;

using NUnit.Framework;

namespace Nethermind.Benchmarks.Core;

public class BloomBenchmark
{
    private LogEntry[] LogEntries = CreateLogs();

    public static LogEntry[] CreateLogs()
    {
        var topics = new Keccak[]
        {
            Keccak.Zero,
            Keccak.EmptyTreeHash,
            Keccak.OfAnEmptySequenceRlp,
            Keccak.OfAnEmptyString
        };

        var logs = new LogEntry[512];
        for (int i = 0; i < logs.Length; i++)
        {
            logs[i] = new LogEntry
            (
                Address.Zero,
                Array.Empty<byte>(),
                topics
            );
        }

        return logs;
    }

    [Benchmark]
    public Bloom ReconstructFromLogs()
    {
        return new Bloom(LogEntries);
    }
}

