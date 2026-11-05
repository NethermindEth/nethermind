// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Xdc;

public interface ISubnetMasternodesCalculator : IMasternodesCalculator
{
    (Address[] NextEpochCandidates, Address[] NextPenalties) GetNextEpochCandidatesAndPenalties(Hash256 parentHash);
}
