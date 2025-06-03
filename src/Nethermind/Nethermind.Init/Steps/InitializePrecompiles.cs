// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;

namespace Nethermind.Init.Steps;

public class InitializePrecompiles(ISpecProvider specProvider, IInitConfig initConfig, ILogManager logManager) : IStep
{
    public async Task Execute(CancellationToken cancellationToken)
    {
        if (specProvider!.GetFinalSpec().IsEip4844Enabled)
        {
            ILogger logger = logManager.GetClassLogger<InitializePrecompiles>();

            try
            {
                await KzgPolynomialCommitments.InitializeAsync(logger, initConfig.KzgSetupPath);
            }
            catch (Exception e)
            {
                if (logger.IsError) logger.Error($"Couldn't initialize {nameof(KzgPolynomialCommitments)} precompile", e);
                throw;
            }
        }
    }
}
