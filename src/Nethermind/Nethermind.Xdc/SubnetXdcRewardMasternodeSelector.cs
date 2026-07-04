// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc;

internal sealed class SubnetXdcRewardMasternodeSelector : IRewardMasternodeSelector
{
    public HashSet<Address> GetRewardMasternodes(XdcBlockHeader checkpointHeader, IXdcReleaseSpec spec) =>
        checkpointHeader.ValidatorsAddress is { } validators
            ? [.. validators]
            : [];
}
