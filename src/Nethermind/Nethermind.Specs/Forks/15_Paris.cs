// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

public class Paris() : NamedReleaseSpec<Paris>(GrayGlacier.Instance)
{
    public override void Apply(ReleaseSpec spec) => spec.Name = "Paris";
    // Note: EIP-3675 uncle ban is pinned on Shanghai and later, not Paris. MainnetSpecProvider
    // returns Paris.Instance for the terminal PoW block (ParisBlockNumber = 15537393) due to an
    // off-by-one in its block-range resolution, so spec-gating at Paris would reject a
    // consensus-valid PoW block. Shanghai is the first boundary that is unambiguously post-merge.
}
