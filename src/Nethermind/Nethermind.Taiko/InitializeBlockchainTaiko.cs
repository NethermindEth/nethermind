// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Producers;
using Nethermind.Init.Steps;

namespace Nethermind.Taiko;

public class InitializeBlockchainTaiko(TaikoNethermindApi api) : InitializeBlockchain(api)
{
    protected override IBlockProductionPolicy CreateBlockProductionPolicy() => NeverStartBlockProductionPolicy.Instance;
}
