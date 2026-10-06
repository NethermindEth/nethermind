// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.State;

public sealed partial class WorldState
{
    // The code the last bytecode access checked: code resolution checks an account's code and then
    // loads the same code by hash, so that load is answered here rather than from the code store again.
    private ValueHash256 _checkedCodeHash;
    private ReadOnlyMemory<byte> _checkedCode;

    /// <summary>Throws when a contract's bytecode is absent from the code store.</summary>
    /// <remarks>
    /// The guest's state is always a witness, so its world state forces the witness-backed code lookup that the
    /// host's stateless decorator adds on top of a world state.
    /// </remarks>
    public void RecordBytecodeAccess(Address address)
    {
        if (!IsContract(address)) return;

        ValueHash256 codeHash = GetCodeHash(address);
        ReadOnlyMemory<byte> code = _stateProvider.GetCode(in codeHash);
        if (code.IsNull())
            throw new InvalidOperationException($"Missing bytecode at address {address}");

        _checkedCodeHash = codeHash;
        _checkedCode = code;
    }

    /// <inheritdoc/>
    /// <remarks>Code is keyed by its own hash, so the code remembered by <see cref="RecordBytecodeAccess"/> cannot go stale.</remarks>
    public ReadOnlyMemory<byte> GetCode(in ValueHash256 codeHash)
    {
        DebugGuardInScope();
        return !_checkedCode.IsNull() && codeHash == _checkedCodeHash ? _checkedCode : _stateProvider.GetCode(in codeHash);
    }
}
