// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Xdc.Spec;

namespace Nethermind.Xdc;

public interface IMasternodesCalculator
{
    (Address[] Masternodes, Address[] PenalizedNodes) CalculateNextEpochMasternodes(long blockNumber, Hash256 parentHash, IXdcReleaseSpec spec);
}
