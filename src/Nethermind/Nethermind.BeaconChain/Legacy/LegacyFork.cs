// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.BeaconChain.Legacy;

/// <summary>The forks before Fulu, in activation order, so a value indexes <see cref="Spec.BeaconChainSpec.Forks"/>.</summary>
internal enum LegacyFork
{
    Phase0,
    Altair,
    Bellatrix,
    Capella,
    Deneb,
    Electra,
}
