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
using Nethermind.Core;

namespace Nethermind.JsonRpc;

public static class JsonRpcUtils
{
    public static async IAsyncEnumerable<JsonParseResult> MultiParseJsonDocument(
        PipeReader pipeReader,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        long maxBufferSize = int.MaxValue
    )
    {
        Console.Error.WriteLine($" MPJD ");
        JsonReaderState defaultState = new JsonReaderState(new JsonReaderOptions()
        {
            AllowMultipleValues = true,
        });

        ReadResult readResult;
        do
        {
            long startTime = Stopwatch.GetTimestamp();
            readResult = await pipeReader.ReadAsync(cancellationToken);
            if (readResult.Buffer.IsEmpty && readResult.IsCompleted || readResult.IsCanceled) break;
            if (readResult.Buffer.Length > maxBufferSize)
            {
                throw new InvalidOperationException("Maximum json buffer size reached.");
            }

            Console.Error.WriteLine($"Read buffer size {readResult.Buffer}");

            bool parsed = false;
            long readSize = 0;
            JsonDocument jsonDocument = null;
            JsonException? jsonException = null;
            try
            {
                Utf8JsonReader reader = new Utf8JsonReader(readResult.Buffer, isFinalBlock: false, state: defaultState);
                parsed = JsonDocument.TryParseValue(ref reader, out jsonDocument);
                readSize = reader.BytesConsumed;
            }
            catch (JsonException ex)
            {
                jsonException = ex;
            }

            if (parsed)
            {
                var slicedBuffer = readResult.Buffer.Slice(readSize);
                pipeReader.AdvanceTo(slicedBuffer.Start);
                yield return new JsonParseResult()
                {
                    ReadSize = readSize,
                    StartTime = startTime,
                    JsonDocument = jsonDocument,
                };
            }
            else if (jsonException is not null)
            {
                yield return new JsonParseResult()
                {
                    ReadSize = readSize,
                    StartTime = startTime,
                    Exception = jsonException,
                    LastBytes = new ReadOnlySequence<byte>(readResult.Buffer.Slice(Math.Max(readResult.Buffer.Length - 1000, 0)).ToArray())
                };
                yield break;
            }
            else
            {
                // Incomplete
                // Need to set tell that the end of buffer has been examined so that next read would
                // get it.
                pipeReader.AdvanceTo(readResult.Buffer.Start, readResult.Buffer.End);
            }
        } while (!readResult.IsCompleted && !cancellationToken.IsCancellationRequested);
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
