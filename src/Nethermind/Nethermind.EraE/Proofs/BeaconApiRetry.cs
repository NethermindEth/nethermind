// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Polly;
using Polly.Retry;

namespace Nethermind.EraE.Proofs;

internal sealed class BeaconApiRetry<T>
{
    private readonly ResiliencePipeline<T> _pipeline;

    internal BeaconApiRetry(int maxAttempts) =>
        _pipeline = new ResiliencePipelineBuilder<T>()
            .AddRetry(new RetryStrategyOptions<T>
            {
                MaxRetryAttempts = maxAttempts - 1,
                ShouldHandle = new PredicateBuilder<T>()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>()
                    .Handle<TimeoutException>(),
                DelayGenerator = args => ValueTask.FromResult<TimeSpan?>(
                    TimeSpan.FromSeconds(Math.Pow(2, args.AttemptNumber + 1)))
            })
            .Build();

    internal Task<T> ExecuteAsync(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        _pipeline.ExecuteAsync(ct => new ValueTask<T>(operation(ct)), cancellationToken).AsTask();
}
