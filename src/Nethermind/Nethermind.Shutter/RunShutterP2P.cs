// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Multiformats.Address;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Shutter.Config;

namespace Nethermind.Shutter;

[RunnerStepDependencies(
    dependencies: [typeof(InitializeNetwork)],
    dependents: [typeof(InitializeBlockProducer)]
)]
public class RunShutterP2P(IShutterConfig shutterConfig, IShutterApi shutterApi, IProcessExitSource exitSource, ILogManager logManager) : IStep
{
    private readonly ILogger _logger = logManager.GetClassLogger<RunShutterP2P>();

    public Task Execute(CancellationToken cancellationToken)
    {
        if (_logger.IsInfo) _logger.Info("Initializing Shutter plugin.");

        IEnumerable<Multiaddress> bootnodeP2PAddresses;
        try
        {
            shutterConfig.Validate(out bootnodeP2PAddresses);
        }
        catch (ArgumentException e)
        {
            throw new ShutterPlugin.ShutterLoadingException("Invalid Shutter config", e);
        }
        _ = shutterApi.StartP2P(bootnodeP2PAddresses, exitSource.Token);

        return Task.CompletedTask;
    }
}
