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
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Caching;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>
///
/// An implementation of `IAggregateChainQueryStore` that works by storing a forest of section where each section
/// is a contiguous chain of nodes with no known branch in between. When a branch happens, a section will split into
/// three, a parent section and two child section each becoming a new chain.
/// 
/// Each node can have a value (or nothing),
/// Each Section have an
///     - Aggregate value.
///     - Chain Aggregate value.
/// On a node value update, `OnValueSet` will be called to recalculate the section aggregate value,
/// then `AggregateChain` will be called, passing in it's parent's chain aggregate value and it's aggregate value to
/// calculate it's chain aggregate value. Then, recursively, all of it's child will have it's chain aggregate value
/// updated with `AggregateChain`.
///
/// This allows for an aggregate query for a chain that requires data from the root of chain (or at least a far ancestor),
/// without having to go through all nodes, assuming that the section is large, and not much branching happens.
/// Query that is specific to a node within section however, is not automatically optimized. You'll need a fancy
/// TAggregate to make it work. Or shortcut it via node index probably.
///
/// QueryUpTo should be O(1) + whatever AggregateWithSubsection complexity is.
/// SetChildParent is O(n) where n is the size of child section. 
/// SetValue is O(n) where n is the number of descendent section.
/// 
/// Assuming most operation is on newer shorter chain/branch, it should not do many operation. Sections are pointed at by an
/// LRU of accessed key. Old section which is not pointed by any LRU entry will get garbage collected. Together
/// with a max section size option, really old node will get garbage collected, an update to those node will create
/// new unconnected section which will not get propagated to its descendent until it is reconnected.
/// Set a high number top prevent or reduce the likelihood of that happening.
///
/// </summary>
public abstract class SectionTreeWithAggregate<TKey, TValue, TAggregate, TChainAggregate> : IAggregateChainQueryStore<TKey, TValue, TChainAggregate> 
{
    protected class Section
    {
        internal TChainAggregate? ParentChainAggregate { get; set; }
        internal TChainAggregate? ChainAggregate  { get; set; }
        internal TAggregate? SectionAggregate  { get; set; }
        
        // Storing as a dictionary instead of table for fast IsChildParentInSection
        internal Dictionary<TKey, int> ItemIdx { get; set; } = new();
        internal Dictionary<TKey, TValue> Values { get; set; } = new();
        internal HashSet<Section> ChildSections { get; set; } = new();

        public bool IsChildParentInSection(TKey child, TKey parent)
        {
            if (ItemIdx.TryGetValue(child, out int childIdx) && ItemIdx.TryGetValue(parent, out int parentIdx))
            {
                return (parentIdx - childIdx) == 1;
            }

            return false;
        }

        public bool IsTail(TKey hash)
        {
            return ItemIdx[hash] == ItemIdx.Count - 1;
        }

        public bool IsHead(TKey hash)
        {
            return ItemIdx[hash] == 0;
        }
    }

    private readonly int _maxSectionSize;
    private readonly LruCache<TKey, Section> _blockSection;
    private int _totalCreatedSection = 0;

    public int TotalCreatedSection { get => _totalCreatedSection; }
    
    public SectionTreeWithAggregate(int maxKeyHandle, int maxSectionSize)
    {
        _blockSection = new LruCache<TKey, Section>(maxKeyHandle, "SectionTreeWithAggregate");
        _maxSectionSize = maxSectionSize;
    }

    private bool isReachableFrom(Section currentNode, Section target)
    {
        if (currentNode == target)
        {
            return true;
        }

        foreach (Section currentNodeChildSection in currentNode.ChildSections)
        {
            if (isReachableFrom(currentNodeChildSection, target))
            {
                return true;
            }
        }

        return false;
    }
    
    public void SetChildParent(TKey child, TKey parent)
    {
        if (EqualityComparer<TKey>.Default.Equals(child, parent))
        {
            throw new ArgumentException("Child and parent cannot be the same");
        }
        
        bool hasChildSection = _blockSection.TryGet(child, out Section? childSection);
        bool hasParentSection = _blockSection.TryGet(parent, out Section? parentSection);
        
        if (!hasChildSection && !hasParentSection)
        {
            // Completely new section that is not attached to anything
            NewSection(parent, child);
            return;
        }

        if (hasChildSection && hasParentSection && childSection == parentSection && childSection.IsChildParentInSection(child, parent))
        {
            // Already in parent child
            return;
        }

        if (hasChildSection && hasParentSection && parentSection.IsTail(parent) && childSection.IsHead(child) && parentSection.ChildSections.Contains(childSection))
        {
            // Already in parent child
            return;
        }
            

        if (hasChildSection && hasParentSection)
        {
            if (isReachableFrom(parentSection, childSection))
            {
                throw new InvalidOperationException(
                    "Child section is already reachable from parent. Only Tree is supported, not a DAG.");
            }
            if (isReachableFrom(childSection, parentSection))
            {
                throw new InvalidOperationException(
                    "Parent section is reachable from child. This can cause a cycle which is not supported here.");
            }
        }

        if (hasParentSection)
        {
            // Now parentSection's tail is definitely parent
            SplitSectionFromHash(parentSection, parent, true);
        }
        else
        {
            parentSection = NewSection(parent);
        }
        
        if (hasChildSection || parentSection.ChildSections.Count > 0 || parentSection.ItemIdx.Count >= _maxSectionSize)
        {
            // Now the childSection's head is definitely child
            if (childSection == null)
            {
                // Parent section have child section > 1, so a new child section need to be attached,
                // but no child section is created for the child, since its new.
                childSection = NewSection(child);
            }
            else
            {
                childSection = SplitSectionFromHash(childSection, child);
            }
            
            AttachSection(parentSection, childSection);
        }
        else
        {
            AppendToSection(parentSection, child);
            PropagateChainAggregate(parentSection);
        }
    }

    /// <summary>
    /// Split the section into two where the new section will start from hash (or after if exclusive is true)
    /// If the new section is empty, null is retunred
    /// </summary>
    private Section? SplitSectionFromHash(Section section, TKey hash, bool exclusive = false)
    {
        if (!section.ItemIdx.TryGetValue(hash, out int newHeadIdx))
        {
            throw new ArgumentException($"Hash {hash} not in section");
        }

        if (exclusive)
        {
            newHeadIdx++;
        }

        if (section.ItemIdx.Count == newHeadIdx)
        {
            return null;
        }

        if (newHeadIdx == 0)
        {
            return section;
        }
        
        Section newSection = new();
        _totalCreatedSection++;
        
        newSection.ItemIdx = section.ItemIdx
            .Where((it) => it.Value >= newHeadIdx)
            .ToDictionary((it) => it.Key, it => it.Value - newHeadIdx);
        
        newSection.Values = section.Values
            .Where((it) => newSection.ItemIdx.ContainsKey(it.Key))
            .ToDictionary((it) => it.Key, it => it.Value);
        
        foreach (KeyValuePair<TKey,int> newSectionItem in newSection.ItemIdx)
        {
            section.ItemIdx.Remove(newSectionItem.Key);
            section.Values.Remove(newSectionItem.Key);
            _blockSection.Set(newSectionItem.Key, newSection);
        }

        // Forwarding childs
        newSection.ChildSections = section.ChildSections;
        section.ChildSections = new HashSet<Section> { newSection };

        (TAggregate? parentAggregate, TAggregate? childAggregate) newAggregate = OnSplit(section.SectionAggregate, section, newSection);
        section.SectionAggregate = newAggregate.parentAggregate;
        newSection.SectionAggregate = newAggregate.childAggregate;

        section.ChainAggregate =
            RecalculateChainAggregate(section.ParentChainAggregate, section.SectionAggregate, section);
        newSection.ParentChainAggregate = section.ChainAggregate;
        newSection.ChainAggregate =
            RecalculateChainAggregate(newSection.ParentChainAggregate, newSection.SectionAggregate, newSection);

        return newSection;
    }

    private void AttachSection(Section parentSection, Section childSection)
    {
        if (parentSection.ChildSections.Contains(childSection))
        {
            return;
        }
        if (parentSection.ChildSections.Count == 0 && parentSection.ItemIdx.Count + childSection.ItemIdx.Count < _maxSectionSize)
        {
            // We merge the two section.
            foreach ((TKey itemHash, int _) in childSection.ItemIdx)
            {
                childSection.Values.TryGetValue(itemHash, out TValue? value);
                AppendToSection(parentSection, itemHash, value);
            }

            parentSection.ChildSections = childSection.ChildSections;
            PropagateChainAggregate(parentSection);
        }
        else
        {
            parentSection.ChildSections.Add(childSection);
            childSection.ParentChainAggregate = parentSection.ChainAggregate;
            PropagateChainAggregate(parentSection);
        }
    }
    
    private void AppendToSection(Section parentSection, TKey key, TValue? value = default)
    {
        parentSection.ItemIdx.Add(key, parentSection.ItemIdx.Count);
        if (value != null)
        {
            parentSection.Values.Add(key, value);
        }
        _blockSection.Set(key, parentSection);
        parentSection.SectionAggregate = OnValueSet(parentSection, key, value, default);
    }


    private Section NewSection(params TKey[] items)
    {
        Section newSection = new();
        _totalCreatedSection++;
        
        foreach (TKey key in items)
        {
            AppendToSection(newSection, key, default);
        }

        newSection.ChainAggregate = RecalculateChainAggregate(default, newSection.SectionAggregate, newSection);
        return newSection;
    }

    public void SetValue(TKey key, TValue value)
    {
        TValue? prevValue = default;
        if (_blockSection.TryGet(key, out Section section))
        {
            section.Values.TryGetValue(key, out prevValue);
        }
        else
        {
            section = NewSection(key);
        }
        
        section.SectionAggregate = OnValueSet(section, key, value, prevValue);
        PropagateChainAggregate(section);
    }

    private void PropagateChainAggregate(Section section)
    {
        section.ChainAggregate = RecalculateChainAggregate(section.ParentChainAggregate, section.SectionAggregate, section);

        foreach (Section sectionChildSection in section.ChildSections)
        {
            sectionChildSection.ParentChainAggregate = section.ChainAggregate;
            PropagateChainAggregate(sectionChildSection);
        }
    }

    public TChainAggregate? QueryUpTo(TKey key)
    {
        if (_blockSection.TryGet(key, out Section section))
        {
            return AggregateWithSubSection(section, key);
        }

        return default;
    }

    /// <summary>
    /// Called when a new value is set for a node. Implementation is expected to return an update section aggregate
    /// </summary>
    /// <param name="section"></param>
    /// <param name="key"></param>
    /// <param name="newValue"></param>
    /// <param name="prevValue"></param>
    protected abstract TAggregate? OnValueSet(Section section, TKey key, TValue? newValue, TValue? prevValue);
    
    /// <summary>
    /// Called when a node is split into two. Used to recalculate new aggregate for both section which should be
    /// returned.
    /// value accordingly.
    /// </summary>
    /// <param name="parentSection"></param>
    /// <param name="childSection"></param>
    protected abstract (TAggregate? parentAggregate, TAggregate? childAggregate) OnSplit(TAggregate? oldParentAggregate, Section parentSection, Section childSection);
    
    /// <summary>
    /// Called when an ancestor was updated, or after `OnValueSet` and `OnSplit`. ParentChainAggregate for the section
    /// was updated. Implementation is expected to calculate a new chain aggregate for the section.
    /// </summary>
    /// <param name="section"></param>
    /// <returns></returns>
    protected abstract TChainAggregate? RecalculateChainAggregate(TChainAggregate? parentChainAggregate, TAggregate? sectionAggregate, Section section);
    
    /// <summary>
    /// Called when `QueryUpTo` is called. Implementation is expected to calculate a more accurate chain aggregate
    /// based on the section's parent chain aggregate, and the aggregate of the chain aggregate.
    /// </summary>
    /// <param name="section"></param>
    /// <param name="key"></param>
    /// <returns></returns>
    protected abstract TChainAggregate? AggregateWithSubSection(Section section, TKey key);
}
