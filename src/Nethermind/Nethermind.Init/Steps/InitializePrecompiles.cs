// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;

namespace Nethermind.Init.Steps;

public class InitializePrecompiles(ISpecProvider specProvider, IInitConfig initConfig, ILogManager logManager, IProcessExitSource processExitSource) : IStep
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
            catch (KzgSetupUnavailableException e)
            {
                if (logger.IsError)
                    logger.Error($"Cannot start: {e.Message} Restore kzg_trusted_setup.txt from the Nethermind distribution " +
                        "or correct Init.KzgSetupPath, check file permissions, and restart the node.");
                processExitSource.Exit(ExitCodes.MissingPrecompile);
                // Cancel dependent steps without the step manager logging the failure again.
                throw new OperationCanceledException(processExitSource.Token);
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
