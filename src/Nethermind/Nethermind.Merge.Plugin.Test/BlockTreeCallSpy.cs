// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;

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

        // DispatchProxy cannot marshal params ReadOnlySpan<Block>; forward explicitly.
        if (targetMethod?.Name == nameof(IBlockTree.TryUpdateMainChain) && args is { Length: >= 2 })
        {
            BlockHeader newHead = (BlockHeader)args[0]!;
            bool wereProcessed = (bool)args[1]!;
            bool forceUpdateHeadBlock = args.Length > 2 && args[2] is bool force && force;
            ReadOnlySpan<Block> preloaded = default;
            if (args.Length > 3)
            {
                if (args[3] is Block[] blocks)
                    preloaded = blocks;
                else if (args[3] is Block block)
                    preloaded = new[] { block };
            }

            return _inner.TryUpdateMainChain(newHead, wereProcessed, forceUpdateHeadBlock, preloaded);
        }

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
