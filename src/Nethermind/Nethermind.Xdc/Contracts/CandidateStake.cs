// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using System.Diagnostics;

namespace Nethermind.Xdc.Contracts;

[DebuggerDisplay("{Address} {Stake}")]
internal struct CandidateStake
{
    public Address Address;
    public UInt256 Stake;
}
