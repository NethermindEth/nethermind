// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Xdc.Types;

public class SubnetSnapshot : Snapshot
{
    public Address[] NextEpochPenalties { get; set; }

    public SubnetSnapshot(ulong number, Hash256 hash, Address[] validators) : base(number, hash, validators) => NextEpochPenalties = [];

    public SubnetSnapshot(ulong number, Hash256 hash, Address[] validators, Address[] penalties) : base(number, hash, validators) => NextEpochPenalties = penalties ?? [];
}
