// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm;

internal sealed partial class StackPool
{
    // Also have parallel prewarming and Rpc calls
    private const int MaxStacksPooled = VirtualMachineStatics.MaxCallDepth * 2;
    public const int StackLength = (EvmStack.MaxStackSize + EvmStack.RegisterLength) * 32;

    private readonly struct StackItem(byte[] dataStack)
    {
        public readonly byte[] DataStack = dataStack;
    }

    public partial void ReturnStacks(byte[] dataStack);

    public partial byte[] RentStacks();
}
