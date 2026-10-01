// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Serialization.Json;
using Nethermind.Core.Crypto;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using System.IO.Abstractions;

namespace Nethermind.Blockchain.Tracing.GethStyle;

public class GethLikeBlockFileTracer : BlockTracerBase<GethLikeTxTrace, GethLikeTxFileTracer>, IDisposable
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    private readonly Block _block;
    private Stream? _file;
    private GethLikeTxFileTracer? _txTracer;
    private readonly string _fileNameFormat;
    private readonly List<string> _fileNames = [];
    private readonly IFileSystem _fileSystem;
    private Utf8JsonWriter? _jsonWriter;
    private readonly GethTraceOptions _options;
    private readonly IReleaseSpec _spec;
    private readonly JsonSerializerOptions _serializerOptions = new();

    /// <summary>
    /// Creates a file tracer for the transactions in a block.
    /// </summary>
    /// <param name="block">Block being traced.</param>
    /// <param name="options">Geth trace configuration.</param>
    /// <param name="fileSystem">File system used to write the trace files.</param>
    /// <param name="spec">Active specification used for intrinsic gas and self-destruct refunds.</param>
    public GethLikeBlockFileTracer(
        Block block,
        GethTraceOptions options,
        IFileSystem fileSystem,
        IReleaseSpec spec) : base(options?.TxHash)
    {
        _block = block ?? throw new ArgumentNullException(nameof(block));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));

        string hash = _block.Hash.Bytes[..4].ToHexString(true);

        _fileNameFormat = _fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), $"block_{hash}-{{0}}-{{1}}-{{2}}.jsonl");

        _serializerOptions.Converters.Add(new GethLikeTxTraceJsonLinesConverter());
        _serializerOptions.Converters.Add(new FileTraceSummaryConverter());
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
        GethLikeTxTrace trace = txTracer.BuildResult();

        if (!LimitReached)
        {
            JsonSerializer.Serialize(_jsonWriter,
                new FileTraceSummary(trace.ReturnValue, trace.Gas),
                _serializerOptions);
            GethLikeTxTraceJsonLinesConverter.WriteLineEnd(_jsonWriter);
        }

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

        ulong? standardIntrinsicGas = TopLevelGasTracker.GetStandardIntrinsicGas(tx, _spec, _block.Header.GasLimit);
        return _txTracer = new(DumpTraceEntry, _options, (long)_spec.GasCosts.DestroyRefund, standardIntrinsicGas);
    }

    private void DisposeFileStreamIfAny()
    {
        _jsonWriter?.Dispose();
        _file?.Dispose();

        _txTracer = null;
        _file = null;
        _jsonWriter = null;
    }

    private bool LimitReached => _options.Limit != 0 && _file is not null && _file.Position > _options.Limit;

    private void DumpTraceEntry(GethTxFileTraceEntry entry)
    {
        if (!LimitReached)
            JsonSerializer.Serialize(_jsonWriter, entry, _serializerOptions);
        if (LimitReached) _txTracer?.StopCapture();
    }

    private string GetFileName(Hash256 txHash)
    {
        string hash = txHash.Bytes[..4].ToHexString(true);
        int index = 0;
        string suffix = string.Create(8, Random.Shared,
            static (chars, rand) =>
            {
                for (int i = 0; i < chars.Length; i++)
                    chars[i] = Alphabet[rand.Next(0, Alphabet.Length)];
            });

        for (; index < _block.Transactions.Length; index++)
        {
            if (_block.Transactions[index].Hash == txHash)
                break;
        }

        return string.Format(_fileNameFormat, index, hash, suffix);
    }

    public void Dispose() => DisposeFileStreamIfAny();
    private readonly record struct FileTraceSummary(ReadOnlyMemory<byte> Output, ulong GasUsed);

    private sealed class FileTraceSummaryConverter : JsonConverter<FileTraceSummary>
    {
        public override FileTraceSummary Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, FileTraceSummary value, JsonSerializerOptions options)
        {
            int length = checked(value.Output.Length * 2);
            byte[]? rented = length > 256 ? ArrayPool<byte>.Shared.Rent(length) : null;
            Span<byte> encoded = rented is null ? stackalloc byte[256] : rented;
            try
            {
                Convert.TryToHexStringLower(value.Output.Span, encoded, out int written);
                writer.WriteStartObject();
                writer.WriteString("output"u8, encoded[..written]);
                writer.WritePropertyName("gasUsed"u8);
                HexWriter.WriteUlongHexStringValue(writer, value.GasUsed);
                writer.WriteEndObject();
            }
            finally
            {
                if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

}
