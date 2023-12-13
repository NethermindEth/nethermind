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

namespace Nethermind.Evm.Tracing.GethStyle;

public class GethLikeBlockFileTracer : BlockTracerBase<GethLikeTxTrace, GethLikeTxFileTracer>
{
    private const string _alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    private readonly Block _block;
    private Stream _file;
    private readonly string _fileNameFormat;
    private readonly List<string> _fileNames = new();
    private readonly IFileSystem _fileSystem;
    private Utf8JsonWriter _jsonWriter;
    private readonly GethTraceOptions _options;
    private readonly JsonSerializerOptions _serializerOptions = new();

    public GethLikeBlockFileTracer(Block block, GethTraceOptions options, IFileSystem fileSystem) : base(options?.TxHash)
    {
        _block = block ?? throw new ArgumentNullException(nameof(block));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        var hash = _block.Hash.Bytes[..4].ToHexString(true);

        _fileNameFormat = _fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), $"block_{hash}-{{0}}-{{1}}-{{2}}.jsonl");

        _serializerOptions.Converters.Add(new GethLikeTxTraceJsonLinesConverter());
    }

    public IReadOnlyCollection<string> FileNames => _fileNames.AsReadOnly();

    public override void EndBlockTrace()
    {
        base.EndBlockTrace();

        DisposeFileStreamIfAny();
    }

    protected override void AddTrace(GethLikeTxTrace trace) { }

    protected override GethLikeTxTrace OnEnd(GethLikeTxFileTracer txTracer)
    {
        var trace = txTracer.BuildResult();

        JsonSerializer.Serialize(_jsonWriter,
            new
            {
                output = trace.ReturnValue.ToHexString(true),
                gasUsed = $"0x{trace.Gas:x}"
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

        _file = _fileSystem.File.OpenWrite(_fileNames.Last());
        _jsonWriter = new(_file);

        return new(DumpTraceEntry, _options);
    }

    private void DisposeFileStreamIfAny()
    {
        _jsonWriter?.Dispose();
        _file?.Dispose();

        _file = null;
        _jsonWriter = null;
    }

    private void DumpTraceEntry(GethTxFileTraceEntry entry) => JsonSerializer.Serialize(_jsonWriter, entry, _serializerOptions);

    private string GetFileName(Hash256 txHash)
    {
        var hash = txHash.Bytes[..4].ToHexString(true);
        var index = 0;
        var suffix = string.Create(8, Random.Shared,
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
