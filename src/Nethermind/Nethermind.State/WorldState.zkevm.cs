// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.State;

public sealed partial class WorldState
{
    /// <summary>Throws when a contract's bytecode is absent from the code store.</summary>
    /// <remarks>
    /// The guest's state is always a witness, so its world state forces the witness-backed code lookup that the
    /// host's stateless decorator adds on top of a world state.
    /// </remarks>
    public void RecordBytecodeAccess(Address address)
    {
        if (IsContract(address) && GetCode(address).IsNull())
            throw new InvalidOperationException($"Missing bytecode at address {address}");
    }
}
