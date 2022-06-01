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
/// An implementation of <see cref="IAggregateChainQueryStore{TKey,TValue,TAggregate}"/> that works by storing a forest
/// of section where each section is a contiguous chain of nodes with no known branch in between. When a branch happens,
/// a section will split into three, a parent section and two child section each becoming a new chain.
/// 
/// Each node can have a value (or nothing),
/// Each Section have an
///     - Aggregate value.
///     - Chain Aggregate value.
/// On a node value update, <see cref="UpdateSectionAggregateOnValueSet"/> will be called to recalculate the section aggregate value,
/// then <see cref="UpdateChainAggregate"/> will be called, passing in it's parent's chain aggregate value and it's aggregate value to
/// calculate it's chain aggregate value. Then, recursively, all of it's child will have it's chain aggregate value
/// updated with <see cref="UpdateChainAggregate"/>.
///
/// This allows for an aggregate query for a chain that requires data from the root of chain (or at least a far ancestor),
/// without having to go through all nodes, assuming that the section is large, and not much branching happens.
/// Query that is specific to a node within section however, is not automatically optimized. You'll need a fancy
/// TAggregate to make it work. Or shortcut it via node index probably.
///
/// <see cref="QueryUpTo"/> should be O(1) + whatever <see cref="AggregateWithSubSection"/> complexity is.
/// <see cref="SetChildParent"/> is O(n) where n is the size of child section. O(1)+O(m) at root or tail of a chain where m
///     is the number of descendent section.
/// <see cref="SetValue"/> is O(m) where m is the number of descendent section.
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

        // A startIdx item to store the start index of an item (for the idx in the ItemIdx). This is done so that
        // Prepend operation is possible which is needed when extending graph from root, like with header sync.
        internal int StartIdx { get; set; } = 0;

        // Storing as a dictionary instead of table for fast check to know if a node in the middle of the section
        // is adjacent to another node. Unfortunately this means we can't iterate from one item to another easily
        internal Dictionary<TKey, int> ItemIdx { get; set; } = new();
        internal Dictionary<TKey, TValue> Values { get; set; } = new();
        
        internal HashSet<Section> ChildSections { get; set; } = new();
        // A weak reference to the parent. Parent pointer is used during attaching two section where the child 
        // section need to detach itself from it's old parent. Using a weak reference because older segment maybe
        // garbage collected
        internal WeakReference<Section> ParentSection { get; set; } = new(null);

        public bool IsChildParentInSection(TKey child, TKey parent)
        {
            if (ItemIdx.TryGetValue(child, out int childIdx) && ItemIdx.TryGetValue(parent, out int parentIdx))
            {
                return (childIdx - parentIdx) == 1;
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

    // For unit testing
    public int TotalCreatedSection { get => _totalCreatedSection; }

    protected SectionTreeWithAggregate(int maxKeyHandle, int maxSectionSize)
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
    
    public virtual void SetChildParent(TKey child, TKey parent)
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
        
        if (hasChildSection && hasParentSection)
        {
            if (parentSection.IsTail(parent) && childSection.IsHead(child) && parentSection.ChildSections.Contains(childSection))
            {
                // Already in parent child
                return;
            }

            if (childSection == parentSection)
            {
                if (childSection.IsChildParentInSection(child, parent))
                {
                    // Already in parent child
                    return;
                }
                
                // Well great. 
                if (parentSection.ItemIdx[parent] < parentSection.ItemIdx[child])
                {
                    // In expected order. Detach child first, then parent, because we don't 
                    // want to remove parent from the main section just yet
                    childSection = SplitSectionFromHash(parentSection, child);
                    SplitSectionFromHash(parentSection, parent, true);
                }
                else
                {
                    // This is weird. But doable. Detach parent first, Then child
                    parentSection = SplitSectionFromHash(childSection, parent);
                    childSection = SplitSectionFromHash(childSection, parent);
                }
            }
            else
            {
                // Now parentSection's tail is definitely parent
                SplitSectionFromHash(parentSection, parent, true);
                // Now childSection's head is definitely child
                childSection = SplitSectionFromHash(childSection, child);
            }
                
            AttachSection(parentSection, childSection);
            return;
        }

        if (hasParentSection)
        {
            // Now parentSection's tail is definitely parent
            SplitSectionFromHash(parentSection, parent, true);
        }

        if (hasChildSection)
        {
            // Now childSection's head is definitely child
            childSection = SplitSectionFromHash(childSection, child);
        }

        bool cannotAppendToParent = hasParentSection && (parentSection.ChildSections.Count > 0 || parentSection.ItemIdx.Count >= _maxSectionSize);
        bool cannotPrependToChild = hasChildSection && (childSection.ParentSection.TryGetTarget(out Section _) || childSection.ItemIdx.Count >= _maxSectionSize);

        if (hasParentSection && !cannotAppendToParent)
        {
            AppendToSection(parentSection, child);
            PropagateChainAggregate(parentSection);
            return;
        }
        
        if (hasChildSection && !cannotPrependToChild)
        {
            PrependToSection(childSection, parent);
            PropagateChainAggregate(childSection);
            return;
        }

        if (parentSection == null)
        {
            parentSection = NewSection(parent);
        }

        if (childSection == null)
        {
            childSection = NewSection(child);
        }
        
        AttachSection(parentSection, childSection);
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
        
        if (section.ItemIdx.Count == newHeadIdx - section.StartIdx)
        {
            return null;
        }

        if (newHeadIdx == section.StartIdx)
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
        newSection.ParentSection.SetTarget(section);

        (TAggregate? parentAggregate, TAggregate? childAggregate) newAggregate = UpdateSectionAggregateOnSplit(section.SectionAggregate, section, newSection);
        section.SectionAggregate = newAggregate.parentAggregate;
        newSection.SectionAggregate = newAggregate.childAggregate;

        section.ChainAggregate =
            UpdateChainAggregate(section.ParentChainAggregate, section.SectionAggregate, section);
        newSection.ParentChainAggregate = section.ChainAggregate;
        newSection.ChainAggregate =
            UpdateChainAggregate(newSection.ParentChainAggregate, newSection.SectionAggregate, newSection);

        return newSection;
    }

    private void AttachSection(Section parentSection, Section childSection)
    {
        if (parentSection.ChildSections.Contains(childSection))
        {
            return;
        }

        if (parentSection.ChildSections.Count == 0 && parentSection.ItemIdx.Count + childSection.ItemIdx.Count <= _maxSectionSize)
        {
            // Merge these two section instead
            AppendChildToParent(parentSection, childSection);
            return;
        }
        
        foreach (Section childSectionChildSection in childSection.ChildSections.ToList())
        {
            if (isReachableFrom(childSectionChildSection, parentSection))
            {
                // For some reason, the parent is a descendent of the child. This is essentially a cycle and the 
                // behaviour is pretty sketchy. This should not happen with a tree, but if it does happen, we detach
                // the child that can reach parent to prevent stackoverflow/infinite loop when recalculating 
                // aggregate.
                childSectionChildSection.ParentSection.SetTarget(null);
                childSection.ChildSections.Remove(childSectionChildSection);
            }
        }
        
        parentSection.ChildSections.Add(childSection);
        if (childSection.ParentSection.TryGetTarget(out Section oldChildParent))
        {
            oldChildParent.ChildSections.Remove(childSection);
        }
        childSection.ParentSection.SetTarget(parentSection);
        childSection.ParentChainAggregate = parentSection.ChainAggregate;
        PropagateChainAggregate(parentSection);
    }
    
    private void AppendChildToParent(Section parentSection, Section childSection)
    {
        // Move item from child to parent. Deleting the child in process.
        foreach ((TKey itemHash, int _) in childSection.ItemIdx)
        {
            childSection.Values.TryGetValue(itemHash, out TValue? value);
            AppendToSection(parentSection, itemHash, value);
        }

        parentSection.ChildSections = childSection.ChildSections;
        if (childSection.ParentSection.TryGetTarget(out Section oldChildParent))
        {
            oldChildParent.ChildSections.Remove(childSection);
        }
        
        PropagateChainAggregate(parentSection);
    }

    
    private void AppendToSection(Section section, TKey key, TValue? value = default)
    {
        section.ItemIdx.Add(key, section.ItemIdx.Count);
        if (value != null)
        {
            section.Values.Add(key, value);
        }
        _blockSection.Set(key, section);
        section.SectionAggregate = UpdateSectionAggregateOnValueSet(section, key, value, default);
    }

    private void PrependToSection(Section section, TKey key, TValue? value = default)
    {
        section.ItemIdx.Add(key, --section.StartIdx);
        if (value != null)
        {
            section.Values.Add(key, value);
        }
        _blockSection.Set(key, section);
        section.SectionAggregate = UpdateSectionAggregateOnValueSet(section, key, value, default);
    }

    private Section NewSection(params TKey[] items)
    {
        Section newSection = new();
        _totalCreatedSection++;
        
        foreach (TKey key in items)
        {
            AppendToSection(newSection, key, default);
        }

        newSection.ChainAggregate = UpdateChainAggregate(default, newSection.SectionAggregate, newSection);
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
        
        section.SectionAggregate = UpdateSectionAggregateOnValueSet(section, key, value, prevValue);
        PropagateChainAggregate(section);
    }

    private void PropagateChainAggregate(Section section)
    {
        section.ChainAggregate = UpdateChainAggregate(section.ParentChainAggregate, section.SectionAggregate, section);

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
    /// Called when a new value is set for a node. Implementation is expected to return an updated section aggregate
    /// </summary>
    /// <param name="section"></param>
    /// <param name="key"></param>
    /// <param name="newValue"></param>
    /// <param name="prevValue"></param>
    protected abstract TAggregate? UpdateSectionAggregateOnValueSet(Section section, TKey key, TValue? newValue, TValue? prevValue);
    
    /// <summary>
    /// Called when a node is split into two. Used to recalculate new aggregate for both section which should be
    /// returned.
    /// </summary>
    /// <param name="parentSection"></param>
    /// <param name="childSection"></param>
    protected abstract (TAggregate? parentAggregate, TAggregate? childAggregate) UpdateSectionAggregateOnSplit(TAggregate? oldParentAggregate, Section parentSection, Section childSection);
    
    /// <summary>
    /// Called when an ancestor was updated, or after setting value or split or if ParentChainAggregate for the section
    /// was updated. Implementation is expected to calculate a new chain aggregate for the section.
    /// </summary>
    /// <param name="section"></param>
    /// <returns></returns>
    protected abstract TChainAggregate? UpdateChainAggregate(TChainAggregate? parentChainAggregate, TAggregate? sectionAggregate, Section section);
    
    /// <summary>
    /// Called when `QueryUpTo` is called. Implementation is expected to calculate a more accurate chain aggregate
    /// based on the section's parent chain aggregate, and the aggregate of the chain aggregate.
    /// </summary>
    /// <param name="section"></param>
    /// <param name="key"></param>
    /// <returns></returns>
    protected abstract TChainAggregate? AggregateWithSubSection(Section section, TKey key);
}
