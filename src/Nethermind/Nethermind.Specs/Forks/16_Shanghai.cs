// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Specs.Forks;

public class Shanghai() : NamedReleaseSpec<Shanghai>(Paris.Instance)
{
    public override void Apply(NamedReleaseSpec spec)
    {
        spec.Name = "Shanghai";
        spec.IsEip3651Enabled = true;
        spec.IsEip3855Enabled = true;
        spec.IsEip3860Enabled = true;
        spec.IsEip4895Enabled = true;
        spec.WithdrawalTimestamp = MainnetSpecProvider.ShanghaiBlockTimestamp;
        // EIP-3675: uncles are forbidden from the merge onwards. Pinned here (not Paris) because
        // MainnetSpecProvider maps the terminal PoW block to Paris.Instance, so spec-gating at
        // Paris would reject a consensus-valid pre-merge block. Shanghai is unambiguously post-merge.
        spec.MaximumUncleCount = 0;
    }
}
