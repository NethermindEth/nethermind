// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Init.Steps;

namespace Nethermind.Taiko;

public class InitializeBlockchainTaiko(
    TaikoNethermindApi api,
    TaikoBeaconHeadAdvancer headAdvancer)
    : InitializeBlockchain(api)
{
    [SuppressMessage("CodeQuality", "IDE0052:Remove unread private member", Justification = "The dependency makes the container activate the head advancer with this step.")]
    private readonly TaikoBeaconHeadAdvancer _headAdvancer = headAdvancer;
}
