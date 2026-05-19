// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Json;

namespace Nethermind.Merge.Plugin.Benchmark;

/// <summary>
/// Measures the direct JSON writer used by engine_getBlobsV2 without method-body work.
/// The returned stats expose response bytes and flush behavior next to BenchmarkDotNet
/// latency/allocation columns.
/// </summary>
[MemoryDiagnoser]
public class BlobsV2DirectResponseBenchmarks
{
    private const int BlobBytes = (int)Eip4844Constants.GasPerBlob;
    private const int ProofBytes = 48;
    private const int RealisticProofsPerBlob = 6;

    private BlobsV2DirectResponse _response = null!;

    [Params(BlobScenario.MissingOne, BlobScenario.PresentOne, BlobScenario.PresentThree)]
    public BlobScenario Scenario { get; set; }

    [Params(false, true)]
    public bool BufferedWriter { get; set; }

    [GlobalSetup]
    public void GlobalSetup() => _response = BuildResponse(Scenario);

    [Benchmark]
    public async ValueTask<WriteStats> WriteToAsync()
    {
        CountingWriter writer = CreateWriter();
        await _response.WriteToAsync(writer, CancellationToken.None);
        await writer.CompleteAsync();

        long flushCount = writer.FlushCount;
        return new WriteStats(
            writer.WrittenCount,
            flushCount,
            writer.FlushTimeMicroseconds,
            flushCount == 0 ? writer.WrittenCount : writer.WrittenCount / flushCount);
    }

    private CountingWriter CreateWriter() =>
        BufferedWriter
            ? new CountingStreamPipeWriter(Stream.Null)
            : new CountingPipeWriter(PipeWriter.Create(Stream.Null, new StreamPipeWriterOptions(leaveOpen: true)));

    private static BlobsV2DirectResponse BuildResponse(BlobScenario scenario)
    {
        int count = scenario switch
        {
            BlobScenario.MissingOne => 1,
            BlobScenario.PresentOne => 1,
            BlobScenario.PresentThree => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
        };

        byte[]?[] blobs = new byte[count][];
        ReadOnlyMemory<byte[]>[] proofs = new ReadOnlyMemory<byte[]>[count];
        for (int i = 0; i < count; i++)
        {
            if (scenario != BlobScenario.MissingOne)
            {
                blobs[i] = CreateBytes(BlobBytes, i + 1);
                proofs[i] = BuildProofs(i + 17);
            }
            else
            {
                proofs[i] = Array.Empty<byte[]>();
            }
        }

        return new BlobsV2DirectResponse(blobs, proofs, count);
    }

    private static byte[][] BuildProofs(int seed)
    {
        byte[][] proofs = new byte[RealisticProofsPerBlob][];
        for (int i = 0; i < proofs.Length; i++)
        {
            proofs[i] = CreateBytes(ProofBytes, seed + i);
        }

        return proofs;
    }

    private static byte[] CreateBytes(int length, int seed)
    {
        byte[] bytes = System.GC.AllocateUninitializedArray<byte>(length);
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(seed + i);
        }

        return bytes;
    }
}

public enum BlobScenario
{
    MissingOne,
    PresentOne,
    PresentThree
}

public readonly record struct WriteStats(
    long BytesWritten,
    long FlushCount,
    long FlushMicroseconds,
    long BytesPerFlush);
