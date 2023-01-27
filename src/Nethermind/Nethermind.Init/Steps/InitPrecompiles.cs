// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Crypto;
using Nethermind.Logging;

namespace Nethermind.Init.Steps;

public class InitPrecompiles : IStep
{
    private readonly INethermindApi _api;

    public InitPrecompiles(INethermindApi api)
    {
        _api = api;
    }

    public async Task Execute(CancellationToken cancellationToken)
    {
        if (_api.SpecProvider!.GetFinalSpec().IsEip4844Enabled)
        {
            ILogger logger = _api.LogManager.GetClassLogger<InitPrecompiles>();

            try
            {
                await KzgPolynomialCommitments.Initialize(logger);
            }
            catch (Exception e)
            {
                if (logger.IsError) logger.Error($"Couldn't initialize {nameof(KzgPolynomialCommitments)} precompile", e);
                throw;
            }
        }
    }
}
