// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Xdc.Contracts;

internal struct CandidateStake
{
    public Address Address;
    public UInt256 Stake;
}
