// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.State;

public interface IReadOnlyStateProvider : IAccountStateProvider
{
    Hash256 StateRoot { get; }

    /// <summary>The code of <paramref name="address"/>; empty for an account without code.</summary>
    /// <returns>The code, or <c>default</c> (<see cref="ReadOnlySpan{T}"/> null) when this provider cannot serve it.</returns>
    /// <remarks>Return empty code as <c>Array.Empty&lt;byte&gt;()</c>: <see cref="ReadOnlyMemory{T}.Empty"/> is <c>default</c>, which callers read as not served.</remarks>
    ReadOnlyMemory<byte> GetCode(Address address);

    /// <summary>The code stored under <paramref name="codeHash"/>.</summary>
    /// <returns>The code, or <c>default</c> (<see cref="ReadOnlySpan{T}"/> null) when this provider cannot serve it; empty code is a non-null empty span.</returns>
    /// <remarks>Return empty code as <c>Array.Empty&lt;byte&gt;()</c>: <see cref="ReadOnlyMemory{T}.Empty"/> is <c>default</c>, which callers read as not served.</remarks>
    ReadOnlyMemory<byte> GetCode(in ValueHash256 codeHash);

    public bool IsContract(Address address);

    bool AccountExists(Address address);

    bool IsDeadAccount(Address address);

    /// <summary>The storage value at <paramref name="storageCell"/> in the state this provider reads.</summary>
    void Get(in StorageCell storageCell, out UInt256 value);

    bool IsDelegatedCode(Address address) => Eip7702Constants.IsDelegatedCode(this.GetCodeSpan(address));
    bool IsDelegatedCode(in ValueHash256 codeHash) => Eip7702Constants.IsDelegatedCode(this.GetCodeSpan(in codeHash));
}
