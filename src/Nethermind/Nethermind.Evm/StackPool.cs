// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm;

internal sealed partial class StackPool
{
    /// <summary>
    /// The process-wide pool. Stacks carry no gas-policy state, so one instance serves every
    /// <see cref="VmState{TGasPolicy}"/> instantiation rather than one per closed type.
    /// </summary>
    /// <remarks>
    /// Must stay the only instance: <see cref="EvmObjectPool{T}"/>'s per-thread free list is static per
    /// pooled type, so a second pool would hand out this one's stacks and would inherit whichever
    /// capacity allocated a given thread's array first.
    /// </remarks>
    public static readonly StackPool Shared = new();

    private StackPool() { }

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
