// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm;

// Lazy, so the guest runs no class constructor for this type.
public readonly ref partial struct TransactionSubstate
{
    public static byte[] ErrorFunctionSelector => field ??= Keccak.Compute("Error(string)").BytesToArray()[..RevertPrefix];
    public static byte[] PanicFunctionSelector => field ??= Keccak.Compute("Panic(uint256)").BytesToArray()[..RevertPrefix];

    private static FrozenDictionary<ulong, string> PanicReasons => field ??= new Dictionary<ulong, string>
    {
        { 0x00, "generic panic" },
        { 0x01, "assert(false)" },
        { 0x11, "arithmetic underflow or overflow" },
        { 0x12, "division or modulo by zero" },
        { 0x21, "enum overflow" },
        { 0x22, "invalid encoded storage byte array accessed" },
        { 0x31, "out-of-bounds array access; popping on an empty array" },
        { 0x32, "out-of-bounds access of an array or bytesN" },
        { 0x41, "out of memory" },
        { 0x51, "uninitialized function" },
    }.ToFrozenDictionary();
}
