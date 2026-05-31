// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks.Dataflow;

namespace Nethermind.RpcTests.Generator;

public static class TestGenerator
{
    public static Task<int> GenerateAsync(ExecutionArgs args, CancellationToken ct) =>
        args.Client is { } client
            ? GenerateViaClientAsync(args, client, ct)
            : GenerateFromReportAsync(args, ct);

    private static async Task<int> GenerateFromReportAsync(ExecutionArgs args, CancellationToken ct)
    {
        Filter filter = new(args);
        ReportReader reader = new(args.Sources, filter);
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

        await reader.ReadIntoAsync(requestsBuffer, ct);
        await writerBlock.Completion;

        return writer.OutputCount;
    }

    private static async Task<int> GenerateViaClientAsync(ExecutionArgs args, Uri client, CancellationToken ct)
    {
        using HttpClient httpClient = new();
        httpClient.Timeout = TimeSpan.FromMinutes(5);

        Filter filter = new(args);
        RequestReader reader = new(args.Sources, filter);
        RequestSender sender = new(client, httpClient);
        await using TestWriter writer = new(filter, args.OutputPath);

        BufferBlock<TestInfo> requestsBuffer = new(new DataflowBlockOptions
        {
            BoundedCapacity = 100_000
        });

        TransformBlock<TestInfo, TestCase> senderBlock = new(
            sender.SendAsync,
            new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = args.Parallelism, BoundedCapacity = args.Parallelism }
        );

        ActionBlock<TestCase> writerBlock = new(
            writer.WriteAsync,
            new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 1, BoundedCapacity = 10_000 }
        );

        requestsBuffer.LinkTo(senderBlock, new DataflowLinkOptions { PropagateCompletion = true });
        senderBlock.LinkTo(writerBlock, new DataflowLinkOptions { PropagateCompletion = true });

        await reader.ReadIntoAsync(requestsBuffer, ct);
        await writerBlock.Completion;
        return writer.OutputCount;
    }
}
