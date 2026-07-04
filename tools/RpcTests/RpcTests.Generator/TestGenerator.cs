// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks.Dataflow;
using Nethermind.RpcTests.Generator.ArchiveIndex;
using SmartFormat;
using SmartFormat.Core.Parsing;

namespace Nethermind.RpcTests.Generator;

public static class TestGenerator
{
    public static Task<int> GenerateAsync(ExecutionArgs args, CancellationToken ct) =>
        args.Client is { } client
            ? GenerateViaClientAsync(args, client, ct)
            : GenerateFromReportAsync(args, ct);

    /// <summary>
    /// Builds archive-index floor-seek probe requests for each base <paramref name="blocks"/> entry, captures the
    /// expected response from <paramref name="client"/>, and writes one test file per request into
    /// <paramref name="outDir"/> as <c>archive-index-&lt;baseBlock&gt;-&lt;queryBlock&gt;.test.json</c>.
    /// </summary>
    public static async Task<int> GenerateArchiveIndexAsync(
        Uri client, IReadOnlyList<long> blocks, string outDir, int parallelism, CancellationToken ct)
    {
        IReadOnlyList<TestInfo> tests = await BuildArchiveIndexTestsAsync(client, blocks, outDir, ct);
        Format outputPath = Smart.Default.Parser.ParseFormat("{FileDir}/{FileName}.test.json");
        Filter filter = new(PermissiveArgs(outputPath));

        return await RunClientPipelineAsync(client, outputPath, parallelism, filter, async (buffer, token) =>
        {
            foreach (TestInfo test in tests)
                await buffer.SendAsync(test, token);
            buffer.Complete();
        }, ct);
    }

    private static async Task<int> GenerateFromReportAsync(ExecutionArgs args, CancellationToken ct)
    {
        Filter filter = new(args);
        await using TestWriter writer = new(filter, args.OutputPath);

        BufferBlock<TestCase> requestsBuffer = new(new DataflowBlockOptions
        {
            BoundedCapacity = 100_000
        });

        ActionBlock<TestCase> writerBlock = new(
            writer.WriteAsync,
            new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 1, BoundedCapacity = 10_000 }
        );

        requestsBuffer.LinkTo(writerBlock, new DataflowLinkOptions { PropagateCompletion = true });

        ReportProcessor processor = new(args.Sources, requestsBuffer, filter);
        await processor.ProcessAllAsync(ct);
        await writerBlock.Completion;

        return writer.OutputCount;
    }

    private static Task<int> GenerateViaClientAsync(ExecutionArgs args, Uri client, CancellationToken ct)
    {
        Filter filter = new(args);
        return RunClientPipelineAsync(client, args.OutputPath, args.Parallelism, filter,
            (buffer, token) => new RequestProcessor(args.Sources, buffer, filter).ProcessAllAsync(token), ct);
    }

    /// <summary>
    /// Runs the fetch-and-write dataflow: <paramref name="produceAsync"/> posts <see cref="TestInfo"/>s into the
    /// buffer (and completes it), each is sent to <paramref name="client"/> and the resulting test case is written.
    /// </summary>
    private static async Task<int> RunClientPipelineAsync(
        Uri client, Format outputPath, int parallelism, Filter filter,
        Func<ITargetBlock<TestInfo>, CancellationToken, Task> produceAsync, CancellationToken ct)
    {
        using HttpClient httpClient = new();
        httpClient.Timeout = TimeSpan.FromMinutes(5);

        RequestSender sender = new(client, httpClient);
        await using TestWriter writer = new(filter, outputPath);

        BufferBlock<TestInfo> requestsBuffer = new(new DataflowBlockOptions
        {
            BoundedCapacity = 100_000
        });

        TransformBlock<TestInfo, TestCase> senderBlock = new(
            info => sender.SendAsync(info, ct),
            new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = parallelism, BoundedCapacity = parallelism }
        );

        ActionBlock<TestCase> writerBlock = new(
            writer.WriteAsync,
            new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 1, BoundedCapacity = 10_000 }
        );

        requestsBuffer.LinkTo(senderBlock, new DataflowLinkOptions { PropagateCompletion = true });
        senderBlock.LinkTo(writerBlock, new DataflowLinkOptions { PropagateCompletion = true });

        await produceAsync(requestsBuffer, ct);
        await writerBlock.Completion;
        return writer.OutputCount;
    }

    private static async Task<IReadOnlyList<TestInfo>> BuildArchiveIndexTestsAsync(
        Uri client, IReadOnlyList<long> blocks, string outDir, CancellationToken ct)
    {
        using RpcClient rpc = new(client);
        ArchiveTxBuilder builder = new(rpc);

        List<TestInfo> tests = [];
        int number = 0;
        foreach (long block in blocks.Distinct())
        {
            IReadOnlyList<ArchiveIndexRequest> requests = await builder.BuildRequestsAsync(block, ct);
            foreach (ArchiveIndexRequest request in requests)
            {
                string path = Path.Combine(outDir, $"archive-index-{block}-{request.QueryBlock}");
                tests.Add(new TestInfo(new FilePos(path, 0), ++number, request.Request));
            }

            Console.WriteLine($"Built {requests.Count} request(s) for base block {block}");
        }

        return tests;
    }

    // archive-index tests are built directly, so no request/response filtering applies.
    private static ExecutionArgs PermissiveArgs(Format outputPath) => new()
    {
        Sources = [],
        Client = null,
        Parallelism = 1,
        Methods = [],
        MinBlocks = null,
        MaxBlocks = null,
        MinResultLen = null,
        OutputPath = outputPath
    };
}
