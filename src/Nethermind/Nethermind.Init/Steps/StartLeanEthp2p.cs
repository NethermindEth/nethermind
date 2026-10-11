// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net.Sockets;
using System.Net.Quic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Core.Exceptions;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.P2P.Subprotocols.Lean.Ethp2p;

namespace Nethermind.Init.Steps;

/// <summary>Starts the EIP-8437 ethp2p QUIC binding of <c>lean/1</c>.</summary>
/// <remarks>
/// When Ethp2p is the common binding, a binding that cannot start stops the node. Otherwise it is optional: the failure is
/// logged and the binding left off.
/// </remarks>
[RunnerStepDependencies(typeof(InitializeNetwork))]
public class StartLeanEthp2p(Lazy<LeanEthp2pHost> host, INetworkConfig config, ILogManager logManager) : IStep
{
    public Task Execute(CancellationToken cancellationToken) =>
        OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()
            ? Start(host.Value.StartAsync, config, logManager.GetClassLogger<StartLeanEthp2p>(), cancellationToken)
            : Task.CompletedTask;

    /// <summary>Runs <paramref name="start"/>; a failure is fatal only when Ethp2p is the common binding.</summary>
    /// <exception cref="InvalidConfigurationException">Ethp2p is the common binding and the binding could not start.</exception>
    public static async Task Start(Func<CancellationToken, Task> start, INetworkConfig config, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            await start(cancellationToken);
        }
        catch (Exception exception) when (exception is QuicException or SocketException or InvalidOperationException or PlatformNotSupportedException)
        {
            if (config.LeanCommonBinding == LeanBinding.Ethp2p)
                throw new InvalidConfigurationException(
                    $"{nameof(INetworkConfig.LeanCommonBinding)} is Ethp2p, but the lean/1 ethp2p binding could not start: {exception.Message}",
                    ExitCodes.ConflictingConfigurations);
            if (logger.IsWarn) logger.Warn((config.LeanBindings & LeanBinding.Rlpx) != 0
                ? $"Failed to start the lean/1 ethp2p binding; lean/1 continues over RLPx: {exception.Message}"
                : $"Failed to start the lean/1 ethp2p binding: {exception.Message}");
        }
    }

    public bool MustInitialize => config.LeanCommonBinding == LeanBinding.Ethp2p;
}
