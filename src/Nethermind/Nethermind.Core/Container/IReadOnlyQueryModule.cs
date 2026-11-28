// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Autofac;
using Autofac.Core;

namespace Nethermind.Core.Container;

/// <summary>The kinds of read-only query an environment serves.</summary>
[Experimental("NMEVM0001")]
[Flags]
public enum ReadOnlyQueryKind
{
    None = 0,
    /// <summary>
    /// Single calls - eth_call, eth_estimateGas and eth_createAccessList, with and without overrides - and every other
    /// user of the shared read-only transaction processor source.
    /// </summary>
    Call = 1,
    /// <summary>
    /// Simulated blocks - eth_simulateV1, debug_simulateV1, trace_simulateV1, and debug_traceCallMany when one of its
    /// bundles carries its own state or block override. Their environments write to an in-memory overlay and a temporary
    /// block tree, and resolve moved precompiles through the code repository and block hashes through the simulated chain.
    /// </summary>
    Simulate = 2,
    /// <summary>
    /// Replays and traced calls - debug_traceCall, debug_traceCallMany when none of its bundles carries its own override
    /// (overrides in its trace options keep it here), debug_traceTransaction and its by-index and in-block variants,
    /// debug_traceBlock, debug_traceBlockByNumber, debug_traceBlockByHash, debug_traceBadBlock, debug_traceBlockFromFile,
    /// debug_intermediateRoots, debug_standardTraceBlockToFile, debug_standardTraceBadBlockToFile, trace_call,
    /// trace_callMany, trace_rawTransaction, trace_replayTransaction, trace_replayBlockTransactions, trace_block,
    /// trace_transaction, trace_get, trace_filter and proof_getTransactionReceipt - including the workers that trace the
    /// transactions of a block in parallel. These environments run whole blocks through the block processor, so the
    /// system calls, rewards and withdrawals of a replayed block and the untraced transactions around a traced one run
    /// there too; the blocks are processed as read-only replays, unvalidated, on a read-only chain, and nothing they
    /// produce is persisted.
    /// </summary>
    Trace = 4,
    /// <summary>Every kind of read-only query environment.</summary>
    All = Call | Simulate | Trace,
}

/// <summary>Marker for modules that configure only the environments serving read-only queries.</summary>
/// <remarks>
/// <para>Register the module itself in the root container as an <see cref="IReadOnlyQueryModule"/>.</para>
/// <para>
/// Every environment loads the modules after its own registrations, so a component a module registers takes the place of
/// the environment's own, while a decorator the environment adds still wraps it.
/// </para>
/// <para>
/// The modules are never applied to main block processing, prewarming, validation or sync. A
/// <see cref="ReadOnlyQueryKind.Call"/> module configures every environment of the shared read-only transaction
/// processor source, though, and other users also run calls there whose results they act on: Taiko builds its transaction
/// lists there, and the Flashbots rbuilder module serves its calls there. A module must therefore leave every result
/// exactly as it would be without it.
/// </para>
/// <para>
/// Some environments that run calls without persisting them are left out on purpose, because their results are acted on
/// or must record every state read: the pool that generates execution witnesses (debug_executionWitness, proof_call and
/// engine_newPayloadWithWitness), Flashbots block validation, the prefix simulator that admits frame transactions to the
/// pool, the XDC masternode calls, receipt regeneration and the history executors.
/// </para>
/// <para>
/// A frame code runner a module registers is offered only the frames of transactions whose tracer does not trace
/// instructions. A transaction traced opcode by opcode - by the struct logger, the prestate, keccak preimage and
/// JavaScript tracers or a Parity vmTrace - runs on the interpreter, while the untraced transactions and system calls
/// of the same block are still offered.
/// </para>
/// </remarks>
[Experimental("NMEVM0001")]
public interface IReadOnlyQueryModule : IModule
{
    /// <summary>The query environments the module configures.</summary>
    ReadOnlyQueryKind AppliesTo => ReadOnlyQueryKind.Call;
}

[Experimental("NMEVM0001")]
public static class ReadOnlyQueryModuleExtensions
{
    /// <summary>Registers the modules among <paramref name="modules"/> that apply to <paramref name="kind"/>.</summary>
    public static ContainerBuilder AddReadOnlyQueryModules(this ContainerBuilder builder, IReadOnlyList<IReadOnlyQueryModule>? modules, ReadOnlyQueryKind kind)
    {
        if (modules is null)
            return builder;
        foreach (IReadOnlyQueryModule module in modules)
        {
            if ((module.AppliesTo & kind) != 0)
                builder.RegisterModule(module);
        }

        return builder;
    }
}
