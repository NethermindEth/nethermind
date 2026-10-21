// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

#pragma warning disable IDE0130 // ILC recognizes the attribute by this name only.
namespace System.Runtime
{
    /// <summary>Binds an <see cref="MethodImplOptions.InternalCall"/> method to the native symbol <paramref name="entry"/>.</summary>
    /// <remarks>
    /// ILC resolves such a method to the symbol itself, with no P/Invoke stub, so its address is the native code's own
    /// and a managed tail call reaches it with the managed calling convention intact.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class RuntimeImportAttribute(string entry) : Attribute
    {
        public string Entry { get; } = entry;
    }
}
#pragma warning restore IDE0130

namespace Nethermind.Evm
{
    /// <summary>The native MUL handler a guest flagged by <see cref="GuestMulFlag"/> assembles.</summary>
    internal static class GuestMul
    {
        /// <summary>
        /// Replaces the second stack word with its product by the top one, modulo 2^256, pops the top one, and
        /// dispatches the next opcode, all on the zkVM's 256-bit multiplication.
        /// </summary>
        /// <remarks>
        /// A guest handler's tail-dispatch target, entered with the handler's arguments, the stack checked and the gas
        /// charged; never called. Being native, it reaches the multiplication without the frame a call from managed code
        /// takes. Each guest's <c>mul.S</c> implements it against the guest's dispatch conventions.
        /// </remarks>
        [MethodImpl(MethodImplOptions.InternalCall)]
        [System.Runtime.RuntimeImport("nethermind_mul_dispatch")]
        public static extern void MultiplyAndDispatch();
    }
}
