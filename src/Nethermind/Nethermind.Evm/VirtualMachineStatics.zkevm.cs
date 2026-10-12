// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Evm;

// Lazy, so the guest runs no class constructor for this type.
public static partial class VirtualMachineStatics
{
    public static PrecompileExecutionFailureException PrecompileExecutionFailureException => field ??= new();
    public static OutOfGasException PrecompileOutOfGasException => field ??= new();
    internal static Address Ripemd160Address => field ??= Address.FromNumber(3);
}
