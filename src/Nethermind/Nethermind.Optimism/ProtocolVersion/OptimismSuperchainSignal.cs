// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.Optimism.ProtocolVersion;

public sealed record OptimismSuperchainSignal(
    OptimismProtocolVersion Recommended,
    OptimismProtocolVersion Required)
{
    public OptimismProtocolVersion Recommended { get; } = Recommended;
    public OptimismProtocolVersion Required { get; } = Required;
}

public sealed record OptimismSignalSuperchainV1Result(
    OptimismProtocolVersion ProtocolVersion)
{
    public OptimismProtocolVersion ProtocolVersion { get; } = ProtocolVersion;
}

public interface IOptimismSignalSuperchainV1Handler
{
    OptimismProtocolVersion CurrentVersion { get; }
    Task OnBehindRecommended(OptimismProtocolVersion recommended);
    Task OnBehindRequired(OptimismProtocolVersion required);
}

public sealed class LoggingOptimismSignalSuperchainV1Handler(
    OptimismProtocolVersion currentVersion,
    ILogManager logManager
) : IOptimismSignalSuperchainV1Handler
{
    private readonly ILogger _logger = logManager.GetClassLogger();
    public OptimismProtocolVersion CurrentVersion { get; init; } = currentVersion;

    public Task OnBehindRecommended(OptimismProtocolVersion recommended)
    {
        _logger.Warn($"Current version {CurrentVersion} is behind recommended version {recommended}");
        return Task.CompletedTask;
    }

    public Task OnBehindRequired(OptimismProtocolVersion required)
    {
        _logger.Error($"Current version {CurrentVersion} is behind required version {required}");
        return Task.CompletedTask;
    }
}
