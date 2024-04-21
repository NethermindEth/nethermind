// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.State;

namespace Nethermind.Blockchain.ValidatorExit;

public interface IValidatorExitEipHandler
{
    ValidatorExit[] ReadWithdrawalRequests(IReleaseSpec spec, IWorldState state);
}
