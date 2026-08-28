// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm;

// Stacks carry no gas-policy state, so one pool serves every VmState{TGasPolicy} instantiation rather
// than one per closed type. Static rather than a singleton instance: EvmObjectPool's local tier is
// static per pooled type, so a second StackPool would hand out this one's stacks - `static` makes that
// inexpressible instead of merely discouraged.
internal static partial class StackPool
{
    // Also have parallel prewarming and Rpc calls
    private const int MaxStacksPooled = VirtualMachineStatics.MaxCallDepth * 2;
    public const int StackLength = (EvmStack.MaxStackSize + EvmStack.RegisterLength) * 32;

    private readonly struct StackItem(byte[] dataStack)
    {
        public readonly byte[] DataStack = dataStack;
    }

    public static partial void ReturnStacks(byte[] dataStack);

    public static partial byte[] RentStacks();
}
