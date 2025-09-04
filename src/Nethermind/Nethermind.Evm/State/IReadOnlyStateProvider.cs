// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm.State;

public interface IReadOnlyStateProvider : IAccountStateProvider
{
    Hash256 StateRoot { get; }

    byte[]? GetCode(Address address);

    byte[]? GetCode(in ValueHash256 codeHash);

    public bool IsContract(Address address);

    bool AccountExists(Address address);

    bool IsDeadAccount(Address address);
}
