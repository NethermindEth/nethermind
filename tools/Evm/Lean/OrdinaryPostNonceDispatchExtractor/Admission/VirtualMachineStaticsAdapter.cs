// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Evm.State;

namespace Nethermind.Evm;

// Compiler-only declaration for the out-of-scope VM body. The extractor validates this
// adapter against the production metadata symbol before it emits any artifact.
public static class VirtualMachineStatics
{
    internal static void RestoreRipemdTouch(IWorldState worldState, IReleaseSpec spec, bool shouldRestore)
    {
    }
}
