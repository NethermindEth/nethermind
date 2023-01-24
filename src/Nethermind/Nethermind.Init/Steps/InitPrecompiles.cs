// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Crypto;

namespace Nethermind.Init.Steps;

public class InitPrecompiles : IStep
{
    private readonly INethermindApi _api;

    public InitPrecompiles(INethermindApi api)
    {
        _api = api;
    }

    public Task Execute(CancellationToken cancellationToken) =>
        _api.SpecProvider!.GetSpec(long.MaxValue, ulong.MaxValue).IsEip4844Enabled
            ? KzgPolynomialCommitments.Initialize()
            : Task.CompletedTask;
}
