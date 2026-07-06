// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Xdc.Contracts;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc;

internal sealed class XdcSubnetRewardCalculator(
    IEpochSwitchManager epochSwitchManager,
    ISpecProvider specProvider,
    IBlockTree blockTree,
    IMasternodeVotingContract masternodeVotingContract,
    ISigningTxCache signingTxCache)
    : XdcEpochRewardCalculator(epochSwitchManager, specProvider, blockTree, masternodeVotingContract, signingTxCache)
{
    protected internal override HashSet<Address> GetRewardMasternodes(XdcBlockHeader checkpointHeader, IXdcReleaseSpec spec) =>
        checkpointHeader.ValidatorsAddress is { } validators
            ? [.. validators]
            : [];
}
