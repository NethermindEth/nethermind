// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.JsonRpc.Test.Modules;

/// <summary>A frame code runner that declines every frame it is offered, recording where each offer was made.</summary>
/// <remarks>Declining leaves every frame to the dispatch loop, so the probe never changes a result.</remarks>
public sealed class FrameOfferProbe : FrameCodeRunner<EthereumGasPolicy>
{
    private readonly ConcurrentQueue<Offer> _offers = new();
    private int _loads;

    /// <summary>One offer: the account the frame executes as - the called account, or the calling frame's own under
    /// DELEGATECALL and CALLCODE - and the machine that offered it.</summary>
    public readonly record struct Offer(Address Account, object Machine);

    public int Offers => _offers.Count;

    /// <summary>How many environments the modules of this probe have been loaded into.</summary>
    public int Loads => Volatile.Read(ref _loads);

    public int OffersFor(Address account) => _offers.Count(offer => offer.Account == account);

    /// <summary>The machines that made the offers recorded so far.</summary>
    public HashSet<object> Machines() => _offers.Select(static offer => offer.Machine).ToHashSet(ReferenceEqualityComparer.Instance);

    public void Clear() => _offers.Clear();

    /// <summary>A query module that attaches this probe to the machines of the environments of <paramref name="appliesTo"/>.</summary>
    public IReadOnlyQueryModule ModuleFor(ReadOnlyQueryKind appliesTo) => new ProbeModule(this, appliesTo);

    protected override EvmExceptionType Run(VirtualMachine<EthereumGasPolicy> vm, CodeInfo codeInfo, OpcodeBodySet bodies,
        scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, ref int opCodesSincePoll, FrameRunFlags flags)
    {
        _offers.Enqueue(new Offer(vm.VmState.Env.ExecutingAccount, vm));
        return Declined;
    }

    private sealed class ProbeModule(FrameOfferProbe probe, ReadOnlyQueryKind appliesTo) : Module, IReadOnlyQueryModule
    {
        public ReadOnlyQueryKind AppliesTo => appliesTo;

        protected override void Load(ContainerBuilder builder)
        {
            Interlocked.Increment(ref probe._loads);
            builder.RegisterInstance(probe).As<FrameCodeRunner<EthereumGasPolicy>>();
        }
    }
}
