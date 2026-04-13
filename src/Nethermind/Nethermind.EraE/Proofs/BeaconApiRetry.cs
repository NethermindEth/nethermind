// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Polly;
using Polly.Retry;

namespace Nethermind.EraE.Proofs;

internal static class BeaconApiRetry
{
    internal static Task<T> RetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        int maxRetries,
        CancellationToken cancellationToken)
    {
        ResiliencePipeline<T> pipeline = new ResiliencePipelineBuilder<T>()
            .AddRetry(new RetryStrategyOptions<T>
            {
                MaxRetryAttempts = maxRetries - 1,
                ShouldHandle = new PredicateBuilder<T>()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>()
                    .Handle<TimeoutException>(),
                DelayGenerator = args => ValueTask.FromResult<TimeSpan?>(
                    TimeSpan.FromSeconds(Math.Pow(2, args.AttemptNumber + 1)))
            })
            .Build();
        return pipeline.ExecuteAsync(async ct => await operation(ct), cancellationToken).AsTask();
    }
}
