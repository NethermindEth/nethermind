// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Evm.State;

public class BalStore : IBlockAccessListBuilder
{
    public bool TracingEnabled { get; set; }
    public BlockAccessList GeneratedBlockAccessList { get; set; } = new();

    public class InvalidBlockLevelAccessListException(BlockHeader block, string message) : InvalidBlockException(block, "InvalidBlockLevelAccessList: " + message);

    private BlockAccessList _suggestedBlockAccessList;
    private long _gasUsed;

    public void LoadSuggestedBlockAccessList(BlockAccessList suggested, long gasUsed)
    {
        GeneratedBlockAccessList = new();
        _suggestedBlockAccessList = suggested;
        _gasUsed = gasUsed;
    }

    public void AddAccountRead(Address address)
    {
        if (TracingEnabled)
        {
            GeneratedBlockAccessList.AddAccountRead(address);
        }
    }

    public long GasUsed()
        => _gasUsed;

    public void ValidateBlockAccessList(BlockHeader block, ushort index, long gasRemaining)
    {
        if (_suggestedBlockAccessList is null)
        {
            return;
        }

        IEnumerator<ChangeAtIndex> generatedChanges = GeneratedBlockAccessList.GetChangesAtIndex(index).GetEnumerator();
        IEnumerator<ChangeAtIndex> suggestedChanges = _suggestedBlockAccessList.GetChangesAtIndex(index).GetEnumerator();

        ChangeAtIndex? generatedHead;
        ChangeAtIndex? suggestedHead;

        int generatedReads = 0;
        int suggestedReads = 0;

        void AdvanceGenerated()
        {
            generatedHead = generatedChanges.MoveNext() ? generatedChanges.Current : null;
            if (generatedHead is not null) generatedReads += generatedHead.Value.Reads;
        }

        void AdvanceSuggested()
        {
            suggestedHead = suggestedChanges.MoveNext() ? suggestedChanges.Current : null;
            if (suggestedHead is not null) suggestedReads += suggestedHead.Value.Reads;
        }

        AdvanceGenerated();
        AdvanceSuggested();

        while (generatedHead is not null || suggestedHead is not null)
        {
            if (suggestedHead is null)
            {
                if (HasNoChanges(generatedHead.Value))
                {
                    AdvanceGenerated();
                    continue;
                }
                throw new InvalidBlockLevelAccessListException(block, $"Suggested block-level access list missing account changes for {generatedHead.Value.Address} at index {index}.");
            }
            else if (generatedHead is null)
            {
                if (HasNoChanges(suggestedHead.Value))
                {
                    AdvanceSuggested();
                    continue;
                }
                throw new InvalidBlockLevelAccessListException(block, $"Suggested block-level access list contained surplus changes for {suggestedHead.Value.Address} at index {index}.");
            }

            int cmp = generatedHead.Value.Address.CompareTo(suggestedHead.Value.Address);

            if (cmp == 0)
            {
                if (generatedHead.Value.BalanceChange != suggestedHead.Value.BalanceChange ||
                    generatedHead.Value.NonceChange != suggestedHead.Value.NonceChange ||
                    generatedHead.Value.CodeChange != suggestedHead.Value.CodeChange ||
                    !Enumerable.SequenceEqual(generatedHead.Value.SlotChanges, suggestedHead.Value.SlotChanges))
                {
                    throw new InvalidBlockLevelAccessListException(block, $"Suggested block-level access list contained incorrect changes for {suggestedHead.Value.Address} at index {index}.");
                }
            }
            else if (cmp > 0)
            {
                if (HasNoChanges(suggestedHead.Value))
                {
                    AdvanceSuggested();
                    continue;
                }
                throw new InvalidBlockLevelAccessListException(block, $"Suggested block-level access list contained surplus changes for {suggestedHead.Value.Address} at index {index}.");
            }
            else
            {
                if (HasNoChanges(generatedHead.Value))
                {
                    AdvanceGenerated();
                    continue;
                }
                throw new InvalidBlockLevelAccessListException(block, $"Suggested block-level access list missing account changes for {generatedHead.Value.Address} at index {index}.");
            }

            AdvanceGenerated();
            AdvanceSuggested();
        }

        if (gasRemaining < (suggestedReads - generatedReads) * GasCostOf.ColdSLoad)
        {
            throw new InvalidBlockLevelAccessListException(block, "Suggested block-level access list contained invalid storage reads.");
        }
    }

    public void SetBlockAccessList(Block block, IReleaseSpec spec)
    {
        if (!spec.BlockLevelAccessListsEnabled)
        {
            return;
        }

        if (block.IsGenesis)
        {
            block.Header.BlockAccessListHash = Keccak.OfAnEmptySequenceRlp;
        }
        else
        {
            block.GeneratedBlockAccessList = GeneratedBlockAccessList;
            block.EncodedBlockAccessList = Rlp.Encode(GeneratedBlockAccessList).Bytes;
            block.Header.BlockAccessListHash = new(ValueKeccak.Compute(block.EncodedBlockAccessList).Bytes);
        }
    }

    private static bool HasNoChanges(in ChangeAtIndex c)
        => c.BalanceChange is null &&
            c.NonceChange is null &&
            c.CodeChange is null &&
            !c.SlotChanges.GetEnumerator().MoveNext();
}
