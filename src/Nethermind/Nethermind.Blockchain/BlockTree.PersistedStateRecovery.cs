// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain
{
    public partial class BlockTree
    {
        private bool _persistedStateRecoveryDone;

        /// <summary>
        /// Absorbs a suggested block already covered by the persisted state so it is never queued
        /// for processing: blocks below the boundary are stored and skipped, the boundary block
        /// fast-forwards the head when its root matches the persisted state. Recovery only applies
        /// while the head lags the persisted state, so it latches off once caught up.
        /// </summary>
        private bool TryAbsorbIntoPersistedState(Block block)
        {
            if (_persistedStateRecoveryDone) return false;

            if (!_stateBoundary.TryGetBestPersistedState(out ulong persistedNumber, out Hash256? persistedRoot))
            {
                return false;
            }

            Block? head = Head;
            if (head is null)
            {
                return false;
            }

            if (head.Number >= persistedNumber)
            {
                _persistedStateRecoveryDone = true;
                return false;
            }

            if (block.Number > persistedNumber)
            {
                return false;
            }

            if (block.Number < persistedNumber)
            {
                if (Logger.IsInfo) Logger.Info($"Skipping processing of {block.ToString(Block.Format.Short)}: its post-state is already part of persisted state {persistedNumber}.");
                return true;
            }

            if (block.StateRoot != persistedRoot)
            {
                if (Logger.IsError) Logger.Error($"Persisted state {persistedNumber} has root {persistedRoot} but suggested block {block.ToString(Block.Format.FullHashAndNumber)} expects {block.StateRoot}; persisted state is on a different fork.");
                return false;
            }

            if (TryUpdateMainChain(block.Header, wereProcessed: true, forceUpdateHeadBlock: true, block))
            {
                if (Logger.IsInfo) Logger.Info($"Fast-forwarded head to {block.ToString(Block.Format.Short)} matching the persisted state.");
            }
            else
            {
                // Deliberately absorbing anyway: normal processing has no state for the parent and
                // would declare the block invalid, deleting it with all its descendants. Later
                // blocks retry the walk.
                if (Logger.IsError) Logger.Error($"Could not fast-forward head to persisted state block {block.ToString(Block.Format.Short)}; a branch predecessor is missing.");
            }

            return true;
        }
    }
}
