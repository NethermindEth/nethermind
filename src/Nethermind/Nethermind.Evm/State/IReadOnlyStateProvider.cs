// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm.State;

public interface IReadOnlyStateProvider : IAccountStateProvider
{
    Hash256 StateRoot { get; }

    byte[]? GetCode(Address address, int? blockAccessIndex = null);

    byte[]? GetCode(in ValueHash256 codeHash);

    public bool IsContract(Address address, int? blockAccessIndex = null);

    bool AccountExists(Address address, int? blockAccessIndex = null);

    bool IsDeadAccount(Address address, int? blockAccessIndex = null);

    bool IsDelegatedCode(Address address) => Eip7702Constants.IsDelegatedCode(GetCode(address));
    bool IsDelegatedCode(in ValueHash256 codeHash) => Eip7702Constants.IsDelegatedCode(GetCode(codeHash));
}
