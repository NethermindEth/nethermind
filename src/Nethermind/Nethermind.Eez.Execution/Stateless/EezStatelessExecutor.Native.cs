// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Eez.Execution.Stateless;

public sealed partial class EezStatelessExecutor
{
    private readonly NativeHistoryContext? _nativeHistory;

    /// <remarks>The caller owns and serializes the L1 evidence for the execution window.</remarks>
    internal EezStatelessExecutor(NativeGenesisSnapshot profile, NativeL1HeaderChain evidence, ILogManager logs)
        : this(profile.Specs, profile.Rules, logs) => _nativeHistory = new NativeHistoryContext(profile, evidence);

    private sealed class NativeHistoryContext(NativeGenesisSnapshot profile, NativeL1HeaderChain evidence)
    {
        private readonly NativeHistorySource? _source = profile.Rules.UsesNativeContract
            ? null
            : new(profile.Rules.Schedule!.L1Reference.ChainId, profile.Specs.ChainId, profile.GenesisHash);

        internal void ValidateEmptyPrefix(BlockHeader header, IWorldState state)
        {
            state.Get(new StorageCell(EezConstants.L1HistoryAddress, (UInt256)2), out UInt256 endpoint);
            if (endpoint > ulong.MaxValue || header.MixHash?.ValueHash256 != evidence.At((ulong)endpoint).PrevRandao)
                throw NativeL1Header.Invalid("empty prefix PREVRANDAO differs from inherited L1 origin");
        }

        internal IBlockProcessor.IBlockTransactionsExecutor Wrap(IBlockProcessor.IBlockTransactionsExecutor inner, IWorldState state) =>
            new NativeHistoryBlockExecutor(inner, state, profile.Specs, profile.Rules, profile.History,
                profile.Rules.Schedule!.L1Reference.Number, _source, evidence);
    }
}
