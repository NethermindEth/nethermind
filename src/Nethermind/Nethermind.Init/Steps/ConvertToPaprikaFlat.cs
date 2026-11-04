// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Init.Modules;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence.PaprikaFlat;

namespace Nethermind.Init.Steps;

[StepCommand("convert-to-paprika-flat", "Copy the flat state of a Flat layout DB into PaprikaFlat and switch the DB to that layout. For development testing.")]
public class ConvertToPaprikaFlat(PaprikaFlatConverter converter, IInitConfig initConfig, ILogManager logManager) : IStep
{
    public Task Execute(CancellationToken cancellationToken) => Task.Run(() =>
    {
        try
        {
            converter.Convert(FlatWorldStateModule.PaprikaFlatDirectory.GetApplicationResourcePath(initConfig.BaseDbPath), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            ILogger logger = logManager.GetClassLogger<ConvertToPaprikaFlat>();
            if (logger.IsInfo) logger.Info("Conversion cancelled by user");
        }
    });
}
