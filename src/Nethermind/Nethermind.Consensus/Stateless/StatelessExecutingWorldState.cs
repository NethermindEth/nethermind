// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.State;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Stateless world state used inside the zkVM guest.
/// </summary>
public class StatelessExecutingWorldState(IWorldState state) : WorldStateDecorator(state)
{
    /// <remarks>
    /// Forces a witness-backed code lookup that throws when the bytecode is absent from the witness.
    /// </remarks>
    public override void RecordBytecodeAccess(Address address)
    {
        if (IsContract(address) && GetCode(address) is null)
            throw new InvalidOperationException($"Missing bytecode at address {address}");
    }
}
