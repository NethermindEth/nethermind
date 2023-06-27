// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Consensus.Validators;
using Nethermind.Init.Steps;

namespace Nethermind.Optimism;

public class InitializeBlockchainOptimism : InitializeBlockchain
{
    private readonly INethermindApi _api;

    public InitializeBlockchainOptimism(INethermindApi api) : base(api)
    {
        _api = api;
    }

    protected override IHeaderValidator CreateHeaderValidator()
        => new OptimismHeaderValidator(
            _api.BlockTree,
            _api.SealValidator,
            _api.SpecProvider,
            _api.LogManager);
}
