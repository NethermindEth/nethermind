// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Init.Steps;

/// <summary>
/// Initializes the Keccak cache.
/// </summary>
[RunnerStepDependencies(typeof(ApplyMemoryHint))]
public class InitKeccakCache : IStep
{
    private readonly INethermindApi _api;

    public InitKeccakCache(INethermindApi api)
    {
        _api = api;
    }

    public Task Execute(CancellationToken cancellationToken)
    {
        IKeccakCacheConfig cacheConfig = _api.Config<IKeccakCacheConfig>();

        KeccakCache.Initialize(cacheConfig.MaxMemory);

        return Task.CompletedTask;
    }
}

/// <summary>
/// Represents the <see cref="KeccakCache"/> configuration.
/// </summary>
[ConfigCategory(HiddenFromDocs = true)]
public interface IKeccakCacheConfig : IConfig
{
    /// <summary>
    /// The maximum memory that <see cref="KeccakCache"/> static shared instance can occupy.
    /// </summary>
    public long MaxMemory { get; set; }
}

public class KeccakCacheConfig : IKeccakCacheConfig
{
    public long MaxMemory { get; set; } = 12.MiB();
}
