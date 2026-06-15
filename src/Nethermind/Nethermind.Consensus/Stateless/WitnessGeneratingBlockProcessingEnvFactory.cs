// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.Stateless;

public interface IWitnessGeneratingBlockProcessingEnvScope : IDisposable
{
    IWitnessGeneratingBlockProcessingEnv Env { get; }
}

public interface IWitnessGeneratingBlockProcessingEnvFactory
{
    IWitnessGeneratingBlockProcessingEnvScope CreateScope();
}

/// <summary>
/// Wraps an Autofac lifetime scope with the witness sandbox session that was armed for its
/// lifetime. <see cref="Dispose"/> disarms the session before tearing the scope down so a
/// subsequent <see cref="WitnessGeneratingBlockProcessingEnvFactory.CreateScope"/> can re-arm
/// cleanly.
/// </summary>
public sealed class ExecutionRecordingScope(ILifetimeScope envLifetimeScope, WitnessCaptureSession session) : IWitnessGeneratingBlockProcessingEnvScope
{
    public IWitnessGeneratingBlockProcessingEnv Env { get; } = envLifetimeScope.Resolve<IWitnessGeneratingBlockProcessingEnv>();

    public void Dispose()
    {
        session.Disarm();
        envLifetimeScope.Dispose();
    }
}

public class WitnessGeneratingBlockProcessingEnvFactory(
    ILifetimeScope rootLifetimeScope,
    IWorldStateManager worldStateManager,
    IDbProvider dbProvider,
    IBlockValidationModule[] validationModules,
    ILogManager logManager) : IWitnessGeneratingBlockProcessingEnvFactory
{
    public IWitnessGeneratingBlockProcessingEnvScope CreateScope()
    {
        IReadOnlyDbProvider readOnlyDbProvider = new ReadOnlyDbProvider(dbProvider, true);

        // Sandbox-local session — separate from the main pipeline's WitnessCaptureSession so the
        // legacy debug_executionWitness re-execution can run without contending on the main one.
        WitnessCaptureSession session = new();

        WitnessCapturingTrieStore trieStore = new(worldStateManager.CreateReadOnlyTrieStore(), session);
        IStateReader stateReader = new StateReader(trieStore, readOnlyDbProvider.CodeDb, logManager);
        IWorldState baseWorldState = new WorldState(
            new TrieStoreScopeProvider(trieStore, readOnlyDbProvider.CodeDb, logManager), logManager);

        IHeaderStore headerStore = rootLifetimeScope.Resolve<IHeaderStore>();
        WitnessTrieStoreRecorder trieRecorder = new();
        WitnessHeaderRecorder headerRecorder = new();
        WitnessCapturingHeaderFinder capturingHeaderFinder = new(headerStore, session);
        WitnessGeneratingWorldState witnessWorldState = new(
            baseWorldState,
            stateReader,
            trieStore,
            trieRecorder,
            headerRecorder,
            headerStore);

        // Arm the session for the sandbox lifetime. Disarm runs in ExecutionRecordingScope.Dispose
        // so the next CreateScope() starts on a clean (disarmed) session.
        session.TryArm(witnessWorldState, headerRecorder, trieRecorder);

        ILifetimeScope envLifetimeScope = rootLifetimeScope.BeginLifetimeScope(builder => builder
            .AddScoped<IStateReader>(stateReader)
            .AddScoped<IWorldState>(witnessWorldState)
            .AddScoped<WitnessGeneratingWorldState>(witnessWorldState)
            .AddScoped<IHeaderFinder>(capturingHeaderFinder)
            .AddScoped<IBlockhashCache, BlockhashCache>()
            .AddScoped<IReceiptStorage>(NullReceiptStorage.Instance)
            .AddScoped<ICodeInfoRepository, CodeInfoRepository>()
            // The whole sandbox re-execution records a witness, so its BlockAccessListManager runs in
            // witness mode unconditionally (sequential + non-caching code reads).
            .AddScoped<WitnessExecutionPredicate>(new WitnessExecutionPredicate(static () => true))
            .AddModule(validationModules)
            .AddScoped<IWitnessGeneratingBlockProcessingEnv, WitnessGeneratingBlockProcessingEnv>());

        return new ExecutionRecordingScope(envLifetimeScope, session);
    }
}
