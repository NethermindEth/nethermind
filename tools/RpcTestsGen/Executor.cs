// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks.Dataflow;

namespace RpcTestsGen;

public class Executor(FileLocation[] sources, Uri[] clientUrls, int parallelism, string? include, string? exclude)
{
    public async Task<string[]> RunAsync(CancellationToken ct)
    {
        using HttpClient httpClient = new();
        httpClient.Timeout = TimeSpan.FromMinutes(5);

        RequestLoader loader = new(sources, include, exclude);
        RequestSender sender = new(clientUrls, httpClient);
        ResponseComparer comparer = new(clientUrls);
        await using TestWriter writer = new();

        BufferBlock<RequestInfo> requestsBuffer = new(new DataflowBlockOptions
        {
            BoundedCapacity = 200_000
        });

        TransformBlock<RequestInfo, ResponseInfo> senderBlock = new(
            sender.SendAsync,
            new ExecutionDataflowBlockOptions {MaxDegreeOfParallelism = parallelism, BoundedCapacity = 10}
        );

        TransformManyBlock<ResponseInfo, TestCase> comparatorBlock = new(
            comparer.Compare,
            new ExecutionDataflowBlockOptions {MaxDegreeOfParallelism = 1, BoundedCapacity = 10}
        );

        ActionBlock<TestCase> writerBlock = new(
            writer.WriteAsync,
            new ExecutionDataflowBlockOptions {MaxDegreeOfParallelism = 1, BoundedCapacity = 10_000}
        );

        requestsBuffer.LinkTo(senderBlock, new DataflowLinkOptions {PropagateCompletion = true});
        senderBlock.LinkTo(comparatorBlock, new DataflowLinkOptions {PropagateCompletion = true});
        comparatorBlock.LinkTo(writerBlock, new DataflowLinkOptions {PropagateCompletion = true});

        await loader.LoadIntoAsync(requestsBuffer, ct);
        await writerBlock.Completion;

        return writer.OutputFiles.ToArray();
    }
}
