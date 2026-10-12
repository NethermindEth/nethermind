// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Evm;

public static partial class VirtualMachineStatics
{
    public static readonly PrecompileExecutionFailureException PrecompileExecutionFailureException = new();
    public static readonly OutOfGasException PrecompileOutOfGasException = new();
    internal static readonly Address Ripemd160Address = Address.FromNumber(3);
}
