// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api.Steps;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.ValidatorClient;

/// <summary>Starts the attester duties when <see cref="IValidatorClientConfig.Enabled"/> is set, once the embedded driver runs.</summary>
/// <remarks>
/// The duties stop with the driver (<see cref="BeaconChainService.Stopping"/>), such as when an external consensus client takes
/// over the engine API, and at shutdown through <see cref="AttesterDutyRunner.StopAsync"/>. Nothing is built while disabled, so a
/// configuration error surfaces only when the duties are enabled.
/// </remarks>
[RunnerStepDependencies(typeof(StartBeaconChain))]
public class StartValidatorClient(IValidatorClientConfig config, Lazy<AttesterDutyRunner> runner, BeaconChainService driver, ILogManager logManager) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        if (!config.Enabled) return Task.CompletedTask;

        _ = runner.Value.Start(driver.Stopping);
        ILogger logger = logManager.GetClassLogger<StartValidatorClient>();
        if (logger.IsInfo) logger.Info("Embedded validator client has been enabled and started");
        return Task.CompletedTask;
    }
}
