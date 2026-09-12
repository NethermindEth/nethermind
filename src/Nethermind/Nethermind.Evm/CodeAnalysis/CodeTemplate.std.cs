// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Evm.CodeAnalysis;

/// <summary>
/// A compiler-emitted bytecode shape recognized in contract code, letting the interpreter skip the
/// opcodes that make it up while charging exactly what executing them would have cost.
/// </summary>
/// <remarks>
/// Recognition is a strict match on the exact opcode sequence: anything that deviates yields
/// <see langword="null"/> and runs through the ordinary dispatch loop. Gas is accumulated while
/// walking the opcodes, so the charge follows from the bytes rather than from a per-shape table.
/// </remarks>
internal sealed partial class CodeTemplate
{
    private CodeTemplate() { }

    private CodeTemplate(MinimalProxy minimalProxy, Address minimalProxyTarget)
    {
        MinimalProxy = minimalProxy;
        MinimalProxyTarget = minimalProxyTarget;
    }

    private CodeTemplate(SelectorDispatch selectorDispatch) => SelectorDispatch = selectorDispatch;

    /// <summary>The shared result for code matching no template, so a resolved-but-empty lookup needs no allocation.</summary>
    public static CodeTemplate None { get; } = new();

    /// <summary>The matched forwarder shape when the code is a minimal proxy; otherwise <see langword="null"/>.</summary>
    public MinimalProxy? MinimalProxy { get; }

    /// <summary>The delegation target when the code is a minimal proxy; otherwise <see langword="null"/>.</summary>
    public Address? MinimalProxyTarget { get; }

    /// <summary>The resolved function-selector table when the code opens with a Solidity dispatcher; otherwise <see langword="null"/>.</summary>
    public SelectorDispatch? SelectorDispatch { get; }

    /// <summary>Recognizes <paramref name="code"/>, returning <see cref="None"/> when it matches no known template.</summary>
    /// <param name="code">The contract's runtime bytecode.</param>
    /// <param name="isValidJumpDestination">Validates a jump target against the code's jump-destination bitmap.</param>
    /// <remarks>
    /// Jump validation is only reached once a template's leading opcodes have matched, so code that is
    /// neither a proxy nor a Solidity dispatcher never forces the jump-destination bitmap to be built.
    /// </remarks>
    public static CodeTemplate Recognize(ReadOnlySpan<byte> code, Func<int, bool> isValidJumpDestination)
    {
        if (MinimalProxy.TryMatch(code, out MinimalProxy? proxy, out Address? proxyTarget))
            return new CodeTemplate(proxy!, proxyTarget!);

        SelectorDispatch? dispatch = SelectorDispatch.TryMatch(code, isValidJumpDestination);
        return dispatch is not null ? new CodeTemplate(dispatch) : None;
    }
}
