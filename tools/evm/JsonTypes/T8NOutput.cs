// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using JsonTypes;
using Nethermind.Core;

namespace Evm.JsonTypes;

public class T8NOutput
{
    public ExecutionResult Result;
    public Dictionary<Address, Account> Alloc;

    public T8NOutput(ExecutionResult result, Dictionary<Address, Account> alloc)
    {
        Result = result;
        Alloc = alloc;
    }
}
