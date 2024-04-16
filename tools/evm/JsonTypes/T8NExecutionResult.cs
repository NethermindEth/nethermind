// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.Tracing.GethStyle.Custom.Native.Prestate;

namespace Evm.JsonTypes;

public class T8NExecutionResult
{
    public readonly PostState PostState;
    public readonly Dictionary<Address, NativePrestateTracerAccount> Alloc;
    public readonly byte[] Body;

    public T8NExecutionResult(PostState postState, Dictionary<Address, NativePrestateTracerAccount> alloc, byte[] body)
    {
        PostState = postState;
        Alloc = alloc;
        Body = body;
    }
}
