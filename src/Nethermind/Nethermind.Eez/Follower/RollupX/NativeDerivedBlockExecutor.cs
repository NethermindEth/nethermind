// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Evm.State;
using Nethermind.State;

namespace Nethermind.Eez.Follower.RollupX;

internal sealed class NativeDerivedBlockExecutor(ILifetimeScope rootLifetime, IWorldStateManager worldStates,
    DerivedBlockBuilder builder, NativeCanonicalHistory history, NativeParentHistory parents, EezChainRules rules) : IDerivedBlockExecutor
{
    public IDerivedBlockSession BeginSession()
    {
        NativeL1HeaderChain captured = history.Read();
        IWorldStateScopeProvider state = worldStates.CreateResettableWorldState();
        ILifetimeScope scope = rootLifetime.BeginLifetimeScope(b => b
            .AddScoped(state)
            .AddScoped(captured)
            .AddScoped<IReceiptStorage>(NullReceiptStorage.Instance)
            .AddScoped<NativeDerivedSuffix>()
            .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, NativeDerivedTransactionsExecutor>());
        IDerivedBlockSession execution = DerivedBlockExecutor.OpenSession(scope, builder);
        return new Session(execution, history, parents, rules, scope.Resolve<NativeDerivedSuffix>(), captured);
    }

    private sealed class Session(IDerivedBlockSession execution, NativeCanonicalHistory history, NativeParentHistory parents,
        EezChainRules rules, NativeDerivedSuffix context, NativeL1HeaderChain captured) : IDerivedBlockSession
    {
        private NativeL1HeaderChain _evidence = captured;
        private L1Anchor? _settlement;

        public async ValueTask<Block> ExecuteAsync(BlockHeader parent, DerivedBlock derived, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (rules.NativeFormat.UsesProxyHistory())
            {
                L1Anchor anchor = derived.NativeHistoryAnchor ?? throw NativeL1Header.Invalid("derived native block has no settlement anchor");
                if (_settlement is { } bound && bound != anchor)
                    throw NativeL1Header.Invalid("derived native session changed settlement anchor");
                NativeHistoryState state = parents.ReadParent(parent);
                if (state.Endpoint < _evidence.FirstNumber || state.Endpoint > _evidence.Endpoint.Number ||
                    anchor.Number < _evidence.FirstNumber || anchor.Number > _evidence.Endpoint.Number)
                    _evidence = await history.Prepare(state.Endpoint, anchor, token);
                NativeL1Header endpoint = _evidence.At(anchor.Number);
                if (endpoint.Hash != anchor.Hash.ValueHash256 || endpoint.Timestamp != anchor.Timestamp ||
                    endpoint.PrevRandao != anchor.MixHash.ValueHash256 || anchor.ParentHash is { } hash && endpoint.ParentHash != hash.ValueHash256)
                    throw NativeL1Header.Invalid("derived native history differs from settlement anchor");
                if (_evidence.Endpoint.Number != anchor.Number) _evidence = _evidence.Range(_evidence.FirstNumber, anchor.Number);
                _settlement = anchor;
                context.Evidence = _evidence;
            }
            token.ThrowIfCancellationRequested();
            return await execution.ExecuteAsync(parent, derived, token);
        }

        public void Dispose() => execution.Dispose();
    }
}
