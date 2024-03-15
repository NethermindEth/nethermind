// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Evm.JsonTypes;

public class T8NExecutionResult
{
    public PostState PostState;
    public Dictionary<Address, Account> Alloc;

    public T8NExecutionResult(PostState postState, Dictionary<Address, Account> alloc)
    {
        PostState = postState;
        Alloc = alloc;
    }
}
