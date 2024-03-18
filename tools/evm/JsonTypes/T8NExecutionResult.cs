// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Evm.JsonTypes;

public class T8NExecutionResult
{
    public readonly PostState PostState;
    public readonly Dictionary<Address, Account> Alloc;
    public readonly byte[] Body;

    public T8NExecutionResult(PostState postState, Dictionary<Address, Account> alloc, byte[] body)
    {
        PostState = postState;
        Alloc = alloc;
        Body = body;
    }
}
