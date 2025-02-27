// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;

namespace Nethermind.JsonRpc;

public static class JsonRpcUtils
{
    public static async IAsyncEnumerable<JsonParseResult> MultiParseJsonDocument(PipeReader pipeReader, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        JsonReaderState defaultState = new JsonReaderState(new JsonReaderOptions()
        {
            AllowMultipleValues = true,
        });

        ReadResult readResult = await pipeReader.ReadAsync(cancellationToken);
        while (!readResult.IsCompleted && !readResult.IsCanceled && !cancellationToken.IsCancellationRequested)
        {
            long startTime = Stopwatch.GetTimestamp();
            var buffer = readResult.Buffer;
            while (!buffer.IsEmpty && !cancellationToken.IsCancellationRequested)
            {
                bool parsed = false;
                long readSize = 0;
                JsonDocument jsonDocument = null;
                JsonException? jsonException = null;
                try
                {
                    Utf8JsonReader reader = new Utf8JsonReader(buffer, isFinalBlock: false, state: defaultState);
                    parsed = JsonDocument.TryParseValue(ref reader, out jsonDocument);
                    readSize = reader.BytesConsumed;
                    buffer = buffer.Slice(reader.Position);
                }
                catch (JsonException ex)
                {
                    jsonException = ex;
                }

                if (jsonException is not null)
                {
                    yield return new JsonParseResult()
                    {
                        ReadSize = readSize,
                        StartTime = startTime,
                        Exception = jsonException,
                        LastBytes = new ReadOnlySequence<byte>(buffer.Slice(Math.Max(buffer.Length - 1000, 0)).ToArray())
                    };
                    yield break;
                }

                if (parsed)
                {
                    yield return new JsonParseResult()
                    {
                        ReadSize = readSize,
                        StartTime = startTime,
                        JsonDocument = jsonDocument,
                    };
                }
                else
                {
                    break;
                }
            }

            pipeReader.AdvanceTo(buffer.Start, buffer.End);
            if (!buffer.IsEmpty)
            {
                readResult = await pipeReader.ReadAsync(cancellationToken);
            }
            else
            {
                readResult = await pipeReader.ReadAtLeastAsync((int)(readResult.Buffer.Length + 1), cancellationToken);
            }
        }
    }
}

public struct JsonParseResult
{
    internal JsonDocument? JsonDocument;
    internal JsonException? Exception;
    internal ReadOnlySequence<byte> LastBytes;
    internal long StartTime;
    internal long ReadSize;
}
