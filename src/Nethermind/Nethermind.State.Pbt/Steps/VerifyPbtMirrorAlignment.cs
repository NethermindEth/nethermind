// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Core.Exceptions;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.State.Pbt.Mirror;
using Nethermind.State.Pbt.Persistence;
using FlatPersistence = Nethermind.State.Flat.Persistence.IPersistence;
using FlatStateId = Nethermind.State.Flat.StateId;

namespace Nethermind.State.Pbt.Steps;

/// <summary>
/// Fails startup unless the mirrored PBT database holds exactly the state the flat one does.
/// </summary>
/// <remarks>
/// Mirroring can only begin from a state both backends can serve, and neither can reach the other's:
/// each prunes everything below its own persisted pointer. Rather than letting that surface as an
/// unservable state mid-processing, it is checked once here. The two get out of step either by being
/// enabled over a flat database PBT was never built from, or by a crash landing between the two
/// persists — see <see cref="PbtFlatDrivenPersistence"/>. Both recover the same way, by rebuilding PBT
/// from the flat database.
/// </remarks>
[RunnerStepDependencies(
    dependencies: [typeof(InitializeBlockTree)],
    dependents: [typeof(InitializeBlockchain)]
)]
public class VerifyPbtMirrorAlignment(
    FlatPersistence flatPersistence,
    IPbtPersistence pbtPersistence,
    ILogManager logManager) : IStep
{
    public Task Execute(CancellationToken cancellationToken)
    {
        using FlatPersistence.IPersistenceReader flatReader = flatPersistence.CreateReader();
        using IPbtPersistence.IReader pbtReader = pbtPersistence.CreateReader();

        FlatStateId flatState = flatReader.CurrentState;
        StateId pbtState = pbtReader.CurrentState;

        StateId expected = flatState == FlatStateId.PreGenesis
            ? StateId.PreGenesis
            : new StateId(flatState.BlockNumber, flatState.StateRoot);

        if (pbtState != expected)
        {
            throw new InvalidConfigurationException(
                $"The mirrored pbt state is at {pbtState} while the flat state is at {flatState}. " +
                $"Mirroring needs both to hold the same state; rebuild the pbt database with {nameof(IPbtConfig)}.{nameof(IPbtConfig.ImportFromPreimageFlat)}, or start from an empty data directory.",
                ExitCodes.ConflictingConfigurations);
        }

        ILogger logger = logManager.GetClassLogger<VerifyPbtMirrorAlignment>();
        if (logger.IsInfo) logger.Info($"Mirroring pbt state against the flat state, both at {flatState}.");

        return Task.CompletedTask;
    }
}
