
// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nethermind.Core.Crypto;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using System.IO.Abstractions;
using Nethermind.Evm.CodeAnalysis.StatsAnalyzer;
using Nethermind.Core.Threading;
using System.Threading.Tasks;

namespace Nethermind.Evm.Tracing.OpcodeStats;

public class OpcodeStatsFileTracer : OpcodeStatsTracer
{
    private const string _alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    private readonly string _fileName;
    private readonly IFileSystem _fileSystem;
    private readonly JsonSerializerOptions _serializerOptions = new();

    public OpcodeStatsFileTracer(int bufferSize, StatsAnalyzer statsAnalyzer, IFileSystem fileSystem) : base(bufferSize,statsAnalyzer)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

        _fileName = _fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), $"op_code_stats.json");
    }

    public override void EndBlockTrace()
    {
        DumpStatsInBackground();
    }


    public override void StartNewBlockTrace(Block block)
    {
        base.StartNewBlockTrace(block);
    }

    private void DumpStats()
    {
            var trace  = BuildResult().First();
            File.WriteAllText(_fileName, String.Empty);
            var _file = _fileSystem.File.OpenWrite(_fileName);
            var jsonWriter = new Utf8JsonWriter(_file);
            JsonSerializer.Serialize(jsonWriter,trace,_serializerOptions);
    }

    private Task DumpStatsInBackground()
    {
        return Task.Run(() => { DumpStats(); });
    }

}
