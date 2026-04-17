// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;

namespace Nethermind.Merge.Plugin.Test;

// DispatchProxy wrapper that forwards every IBlockTree call to an inner instance while
// counting FindHeader invocations. Lets tests observe ancestry-walk depth from the outside.
public class BlockTreeCallSpy : DispatchProxy
{
    private IBlockTree _inner = null!;

    public int FindHeaderCalls { get; private set; }

    public void ResetCounters() => FindHeaderCalls = 0;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IBlockFinder.FindHeader)) FindHeaderCalls++;
        return targetMethod?.Invoke(_inner, args);
    }

    public static (IBlockTree Proxy, BlockTreeCallSpy Spy) Wrap(IBlockTree inner)
    {
        object raw = Create<IBlockTree, BlockTreeCallSpy>()!;
        BlockTreeCallSpy spy = (BlockTreeCallSpy)raw;
        spy._inner = inner;
        return ((IBlockTree)raw, spy);
    }
}
