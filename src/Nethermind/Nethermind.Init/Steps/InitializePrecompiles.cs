// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Crypto;
using Nethermind.Logging;

namespace Nethermind.Init.Steps;

public class InitializePrecompiles : IStep
{
    private readonly INethermindApi _api;

    public InitializePrecompiles(INethermindApi api)
    {
        _api = api;
    }

    public async Task Execute(CancellationToken cancellationToken)
    {
        if (_api.SpecProvider!.GetFinalSpec().IsEip4844Enabled)
        {
            ILogger logger = _api.LogManager.GetClassLogger<InitializePrecompiles>();
            IInitConfig initConfig = _api.Config<IInitConfig>();

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
