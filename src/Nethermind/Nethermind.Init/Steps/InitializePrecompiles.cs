// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;

namespace Nethermind.Init.Steps;

public class InitializePrecompiles : IStep
{
    private ILogger _logger;
    private IInitConfig _initConfig;
    private ISpecProvider _specProvider;

    public InitializePrecompiles(ISpecProvider specProvider, IInitConfig initConfig, ILogger logger)
    {
        _specProvider = specProvider;
        _initConfig = initConfig;
        _logger = logger;
    }

    public async Task Execute(CancellationToken cancellationToken)
    {
        if (_specProvider!.GetFinalSpec().IsEip4844Enabled)
        {
            try
            {
                await KzgPolynomialCommitments.InitializeAsync(_logger, _initConfig.KzgSetupPath);
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"Couldn't initialize {nameof(KzgPolynomialCommitments)} precompile", e);
                throw;
            }
        }
    }
}
