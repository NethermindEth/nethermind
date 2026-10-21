// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Init.Steps;

namespace Nethermind.Taiko;

public class InitializeBlockchainTaiko(
    TaikoNethermindApi api,
    TaikoBeaconHeadAdvancer headAdvancer)
    : InitializeBlockchain(api)
{
    private readonly TaikoBeaconHeadAdvancer _headAdvancer = headAdvancer;
}
