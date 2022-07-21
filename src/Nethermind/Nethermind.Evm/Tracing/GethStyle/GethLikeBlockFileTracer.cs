//  Copyright (c) 2022 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Evm.Tracing.GethStyle;

public class GethLikeBlockFileTracer : BlockTracerBase<GethLikeTxTrace, GethLikeTxFileTracer>
{
    private const string _alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    private readonly Block _block;
    private FileStream _file;
    private readonly string _fileNameFormat;
    private readonly List<string> _fileNames = new();
    private Utf8JsonWriter _jsonWriter;
    private readonly GethTraceOptions _options;
    private readonly JsonSerializerOptions _serializerOptions = new();
    private readonly Stopwatch _stopwatch = new();

    public GethLikeBlockFileTracer(Block block, GethTraceOptions options) : base(options?.TxHash)
    {
        _block = block ?? throw new ArgumentNullException(nameof(block));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        var hash = Bytes.ToHexString(_block.Hash.Bytes[..4], true);

        _fileNameFormat = Path.Combine(Path.GetTempPath(), $"block_{hash}-{{0}}-{{1}}-{{2}}.jsonl");

        _serializerOptions.Converters.Add(new GethLikeTxTraceJsonConverter());
    }

    public IReadOnlyCollection<string> FileNames => _fileNames.AsReadOnly();

    public override void EndBlockTrace() => DisposeFileStreamIfAny();

    public override void StartNewBlockTrace(Block block) { }

    protected override void AddTrace(GethLikeTxTrace trace) { }

    protected override GethLikeTxTrace OnEnd(GethLikeTxFileTracer txTracer)
    {
        var trace = txTracer.BuildResult();

        _stopwatch.Stop();

        JsonSerializer.Serialize(_jsonWriter,
            new
            {
                output = Bytes.ToHexString(trace.ReturnValue, true),
                gasUsed = $"0x{trace.Gas:x}",
                time = _stopwatch.ElapsedTicks
            },
            _serializerOptions);

        DisposeFileStreamIfAny();

        return trace;
    }

    protected override GethLikeTxFileTracer OnStart(Transaction? tx)
    {
        // Ensure the current file stream is disposed in case of API misuse
        DisposeFileStreamIfAny();

        _fileNames.Add(GetFileName(tx.Hash));

        _file = File.OpenWrite(_fileNames.Last());
        _jsonWriter = new Utf8JsonWriter(_file);

        _stopwatch.Restart();

        return new(DumpTraceEntry, _options);
    }

    private void DisposeFileStreamIfAny()
    {
        _jsonWriter?.Dispose();
        _file?.Dispose();

        _file = null;
        _jsonWriter = null;
    }

    private void DumpTraceEntry(GethTxFileTraceEntry entry)
    {
        JsonSerializer.Serialize(_jsonWriter, entry, _serializerOptions);

        _jsonWriter.Flush();
        _jsonWriter.Reset();
        _jsonWriter.WriteRawValue(Environment.NewLine, true);
        _jsonWriter.Flush();
        _jsonWriter.Reset();
    }

    private string GetFileName(Keccak txHash)
    {
        var hash = Bytes.ToHexString(txHash.Bytes[..4], true);
        var index = 0;
        var suffix = string.Create(8, new Random(),
            (chars, rand) =>
            {
                for (var i = 0; i < chars.Length; i++)
                    chars[i] = _alphabet[rand.Next(0, _alphabet.Length)];
            });

        for (; index < _block.Transactions.Length; index++)
            if (_block.Transactions[index].Hash == txHash)
                break;

        return string.Format(_fileNameFormat, index, hash, suffix);
    }
}
