// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks.Dataflow;

namespace RpcTestsGen;

public class Executor(ExecutionArgs args)
{
    public async Task<string[]> RunAsync(CancellationToken ct)
    {
        using HttpClient httpClient = new();
        httpClient.Timeout = TimeSpan.FromMinutes(5);

        Filter filter = new(args);
        RequestReader reader = new(args.Sources, filter);
        RequestSender sender = new(args.Clients, httpClient);
        ResponseComparer comparer = new(args.Clients);
        await using TestWriter writer = new(filter);

        BufferBlock<RequestInfo> requestsBuffer = new(new DataflowBlockOptions
        {
            BoundedCapacity = 100_000
        });

        TransformBlock<RequestInfo, ResponseInfo> senderBlock = new(
            sender.SendAsync,
            new ExecutionDataflowBlockOptions {MaxDegreeOfParallelism = args.Parallelism, BoundedCapacity = 1}
        );

        TransformManyBlock<ResponseInfo, TestCase> comparatorBlock = new(
            comparer.Compare,
            new ExecutionDataflowBlockOptions {MaxDegreeOfParallelism = 1, BoundedCapacity = 1}
        );

        ActionBlock<TestCase> writerBlock = new(
            writer.WriteAsync,
            new ExecutionDataflowBlockOptions {MaxDegreeOfParallelism = 1, BoundedCapacity = 10_000}
        );

        requestsBuffer.LinkTo(senderBlock, new DataflowLinkOptions {PropagateCompletion = true});
        senderBlock.LinkTo(comparatorBlock, new DataflowLinkOptions {PropagateCompletion = true});
        comparatorBlock.LinkTo(writerBlock, new DataflowLinkOptions {PropagateCompletion = true});

        await reader.ReadIntoAsync(requestsBuffer, ct);
        await writerBlock.Completion;

        return writer.OutputFiles.ToArray();
    }
}
