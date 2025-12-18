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
    private static SemaphoreSlim _setupLock = new(1);
    private static bool _wasSetup = false;
    public async Task Execute(CancellationToken cancellationToken)
    {
        if (specProvider!.GetFinalSpec().IsEip4844Enabled)
        {
            ILogger logger = logManager.GetClassLogger<InitializePrecompiles>();

            await _setupLock.WaitAsync(cancellationToken);
            try
            {
                if (!_wasSetup)
                {
                    await KzgPolynomialCommitments.InitializeAsync(logger, initConfig.KzgSetupPath);
                    _wasSetup = true;
                }
            }
            catch (Exception e)
            {
                if (logger.IsError)
                    logger.Error($"Couldn't initialize {nameof(KzgPolynomialCommitments)} precompile", e);
                throw;
            }
            finally
            {
                _setupLock.Release();
            }
        }
    }
}
