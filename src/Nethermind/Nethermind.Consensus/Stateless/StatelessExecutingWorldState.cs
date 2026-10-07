// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Stateless world state the host uses to execute a block against its witness.
/// </summary>
/// <remarks>Not thread-safe: the remembered code is two fields, and the stateless environment executes sequentially.</remarks>
public class StatelessExecutingWorldState(IWorldState state) : WorldStateDecorator(state)
{
    // BENCH (bench/handoff-matches-skip): this decorator keeps its own state layer, so it never claims a key is unchanged.
    public override bool MayHaveChangedInBlock(Address address) => true;

    public override bool MayHaveStorageChangedInBlock(Address address) => true;

    public override bool MayHaveStorageChangedInBlock(in StorageCell cell) => true;

    // The code the last bytecode access checked: code resolution checks an account's code and then
    // loads the same code by hash, so the load is answered here rather than from the code store again.
    private ValueHash256 _checkedCodeHash;
    private ReadOnlyMemory<byte> _checkedCode;

    public override void Set(in StorageCell storageCell, in UInt256 newValue, in UInt256 currentValue)
        => State.Set(in storageCell, in newValue, in currentValue);

    /// <remarks>
    /// Forces a witness-backed code lookup that throws when the bytecode is absent from the witness.
    /// </remarks>
    public override void RecordBytecodeAccess(Address address)
    {
        if (!IsContract(address)) return;

        ValueHash256 codeHash = GetCodeHash(address);
        ReadOnlyMemory<byte> code = State.GetCode(in codeHash);
        if (code.IsNull())
            throw new InvalidOperationException($"Missing bytecode at address {address}");

        _checkedCodeHash = codeHash;
        _checkedCode = code;
    }

    /// <inheritdoc/>
    /// <remarks>Code is keyed by its own hash, so the remembered code cannot go stale.</remarks>
    public override ReadOnlyMemory<byte> GetCode(in ValueHash256 codeHash)
        => !_checkedCode.IsNull() && codeHash == _checkedCodeHash ? _checkedCode : State.GetCode(in codeHash);
}
