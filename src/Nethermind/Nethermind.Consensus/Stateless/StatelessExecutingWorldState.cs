// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.State;

namespace Nethermind.Consensus.Stateless;

public class StatelessExecutingWorldState(IWorldState state) : WorldStateDecorator(state)
{
    /// <remarks>
    /// Resolving the code forces a lookup against the witness-backed code database,
    /// which fails if the bytecode was not included in the witness.
    /// </remarks>
    public override void RecordBytecodeAccess(Address address)
    {
        if (IsContract(address) && GetCode(address) is null)
            throw new InvalidOperationException($"Missing bytecode at address {address}");
    }
}
