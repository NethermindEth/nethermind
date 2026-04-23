// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

public class Paris() : NamedReleaseSpec<Paris>(GrayGlacier.Instance)
{
    public override void Apply(ReleaseSpec spec) => spec.Name = "Paris";
    // Note: the EIP-3675 uncle ban lives on Shanghai, not here. MainnetSpecProvider's
    // GrayGlacier→Paris boundary is `< ParisBlockNumber`, so block 15537393 (the terminal PoW
    // block) falls under Paris.Instance - making Paris a mixed-era spec that covers both the
    // terminal PoW block and the post-merge window up to Shanghai. Pinning MaximumUncleCount=0
    // here would spec-reject a consensus-valid PoW block if it carried uncles.
}
