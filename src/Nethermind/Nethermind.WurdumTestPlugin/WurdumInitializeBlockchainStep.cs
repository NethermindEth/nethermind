// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api;
using Nethermind.Consensus.Producers;
using Nethermind.Init.Steps;

namespace Nethermind.WurdumTestPlugin;

public class WurdumInitializeBlockchainStep(INethermindApi api) : InitializeBlockchain(api)
{
    protected override IBlockProductionPolicy CreateBlockProductionPolicy() => AlwaysStartBlockProductionPolicy.Instance;
}
