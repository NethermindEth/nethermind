//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>
/// Something that tracks if a given hash is on a known invalid chain, as one if it's ancestor have been reported to
/// be invalid.
///
/// As far as section tree is concerned, the nodes may contain a <see cref="Keccak"/> hash which is the parent for an invalid block,
/// if the node is considered invalid. The aggregate for the section and chain is a <see cref="LowestInvalidBlock"/> which contains an
/// InvalidBlock and InvalidBlockParent which is used to store if the section have invalid block from it's parent or not.
/// 
/// </summary>
public class InvalidChainTracker: SectionTreeWithAggregate<Keccak, Keccak, LowestInvalidBlock, LowestInvalidBlock>, IInvalidChainTracker
{
    public InvalidChainTracker(): base(256, 1024) {
    }
    
    public InvalidChainTracker(int maxKeyHandle, int maxSectionSize): base(maxKeyHandle, maxSectionSize) {
    }

    public void OnInvalidBlock(Keccak failedBlock, Keccak parent)
    {
        SetValue(failedBlock, parent);
    }

    public bool IsOnKnownInvalidChain(Keccak blockHash, out Keccak? lastValidHash)
    {
        LowestInvalidBlock? queryResult = base.QueryUpTo(blockHash);

        if (queryResult?.InvalidBlock == null)
        {
            lastValidHash = null;
            return false;
        }

        lastValidHash = queryResult.InvalidBlockParent;
        return queryResult.InvalidBlock != null;
    }

    protected override LowestInvalidBlock? UpdateSectionAggregateOnValueSet(Section section, Keccak key, Keccak? newValue, Keccak? prevValue)
    {
        if (newValue == null)
        {
            // No new value set. Do nothing.
            return section.SectionAggregate;
        }
        
        if (
            section.SectionAggregate?.InvalidBlock == null || // New invalid block known
            section.ItemIdx[key] < section.ItemIdx[section.SectionAggregate.InvalidBlock] // The new invalid block is older
            )
        {
            return new LowestInvalidBlock()
            {
                InvalidBlock = key,
                InvalidBlockParent = newValue,
            };
        }

        return section.SectionAggregate; // No change
    }

    protected override (LowestInvalidBlock? parentAggregate, LowestInvalidBlock? childAggregate) UpdateSectionAggregateOnSplit(LowestInvalidBlock? oldParentAggregate,
        Section parentSection, Section childSection)
    {
        if (oldParentAggregate?.InvalidBlock != null)
        {
            if (parentSection.ItemIdx.ContainsKey(oldParentAggregate.InvalidBlock))
            {
                // Ideally, we update the child with the lowest invalid block in the child
                // But unless someone move the child node to a parent above the current parent, 
                // The end result is the same, the child is in an invalid chain.
                return (oldParentAggregate, null);
            }
            else
            {
                // The parent aggregate is now the child aggregate.
                return (null, oldParentAggregate);
            }
        }

        return (null, null); // Does not matter. No invalid block in parent or child
    }

    protected override LowestInvalidBlock? UpdateChainAggregate(LowestInvalidBlock? parentChainAggregate, LowestInvalidBlock? sectionAggregate,
        Section section)
    {
        if (parentChainAggregate?.InvalidBlock != null) return parentChainAggregate;
        return sectionAggregate;
    }

    protected override LowestInvalidBlock? AggregateWithSubSection(Section section, Keccak key)
    {
        if (section.ParentChainAggregate?.InvalidBlock != null)
        {
            return section.ParentChainAggregate;
        }

        if (section.SectionAggregate?.InvalidBlock != null)
        {
            if (section.ItemIdx[key] >= section.ItemIdx[section.SectionAggregate?.InvalidBlock])
            {
                return section.SectionAggregate;
            }
        }

        return null; // No known invalid block
    }
}

public class LowestInvalidBlock
{
    public Keccak? InvalidBlockParent;
    public Keccak? InvalidBlock;
}
