// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net.Sockets;
using System.Net.Quic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Lean.Ethp2p;

namespace Nethermind.Init.Steps;

/// <summary>Starts the optional EIP-8437 ethp2p QUIC binding of <c>lean/1</c>.</summary>
/// <remarks>A binding that cannot start is logged and left off: <c>lean/1</c> continues over RLPx.</remarks>
[RunnerStepDependencies(typeof(InitializeNetwork))]
public class StartLeanEthp2p(Lazy<LeanEthp2pHost> host, ILogManager logManager) : IStep
{
    public async Task Execute(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows()) return;
        try
        {
            await host.Value.StartAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is QuicException or SocketException or InvalidOperationException)
        {
            ILogger logger = logManager.GetClassLogger<StartLeanEthp2p>();
            if (logger.IsError) logger.Error("Failed to start the lean/1 ethp2p binding; lean/1 continues over RLPx", exception);
        }
    }

    public bool MustInitialize => false;
}
