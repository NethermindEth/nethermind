// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Paprika.Data;
using Paprika.Store;

namespace Paprika.Tests.Store;

/// <summary>
/// Records the visited root's account counter, the <see cref="ChildBottomPage"/>s found under bottom and data pages, the
/// data pages, the pages reached through fan-out data pages, and the pages claimed twice.
/// </summary>
public sealed class TreeShapeVisitor : IPageVisitor, IDisposable
{
    private readonly Stack<(Type Type, int PrefixLength, HashSet<string>? MainKeys)> _parents = new();
    private readonly HashSet<DbAddress> _claimed = [];
    private ReclaimRange _reclaim;

    public uint AccountCounter { get; private set; }

    /// <summary>The number of child pages of <see cref="BottomPage"/>s.</summary>
    public int BottomPageChildren { get; private set; }

    /// <summary>The number of entries, tombstones included, in the <see cref="ChildBottomPage"/> leaves of <see cref="DataPage"/>s.</summary>
    public int DataPageLeafEntries { get; private set; }

    /// <summary>The number of pages a <see cref="DataPage"/> reaches through a two-nibble bucket, that is as a fan-out page.</summary>
    public int FanOutChildren { get; private set; }

    public int DataPages { get; private set; }

    /// <summary>The number of entries of <see cref="BottomPage"/> children whose key is also in their parent's main map, which shadows them.</summary>
    public int ShadowedChildEntries { get; private set; }

    /// <summary>
    /// The number of pages that the tree, the abandoned pages and the pages left for reuse by a clear claim more than once
    /// between them. Each of them would be handed out while still in use.
    /// </summary>
    public int PagesClaimedTwice { get; private set; }

    public static TreeShapeVisitor VisitRoot(PagedDb db)
    {
        TreeShapeVisitor shape = new();
        db.VisitRoot(shape);
        return shape;
    }

    public IDisposable On<TPage>(scoped ref NibblePath.Builder prefix, TPage page, DbAddress addr)
        where TPage : unmanaged, IPage => Visit(page, addr, prefix.Current.Length);

    public IDisposable On<TPage>(TPage page, DbAddress addr) where TPage : unmanaged, IPage => Visit(page, addr, 0);

    public IDisposable Scope(string name) => NoopDisposable.Instance;

    private IDisposable Visit<TPage>(TPage page, DbAddress addr, int prefixLength) where TPage : unmanaged, IPage
    {
        Claim(addr);
        _parents.TryPeek(out (Type Type, int PrefixLength, HashSet<string>? MainKeys) parent);

        if (parent.Type == typeof(DataPage) && prefixLength == parent.PrefixLength + 2)
        {
            FanOutChildren++;
        }

        switch (page)
        {
            case RootPage root:
                AccountCounter = root.Data.AccountCounter;
                _reclaim = root.Data.Reclaim;
                break;
            case AbandonedPage abandoned:
                foreach (DbAddress reusable in abandoned.Enumerate())
                {
                    Claim(reusable);
                }

                break;
            case ChildBottomPage child when parent.Type == typeof(BottomPage):
                BottomPageChildren++;
                foreach (SlottedArray.Item item in child.Map.EnumerateAll())
                {
                    if (parent.MainKeys!.Contains(item.Key.ToString()))
                    {
                        ShadowedChildEntries++;
                    }
                }

                break;
            case ChildBottomPage child when parent.Type == typeof(DataPage):
                DataPageLeafEntries += child.Map.Count;
                break;
            // Abandoned pages never dispose their scope, so push only the parents reported on.
            case BottomPage bottom:
                HashSet<string> mainKeys = [];
                foreach (SlottedArray.Item item in bottom.Map.EnumerateAll())
                {
                    mainKeys.Add(item.Key.ToString());
                }

                _parents.Push((typeof(BottomPage), prefixLength, mainKeys));
                return this;
            case DataPage:
                DataPages++;
                _parents.Push((typeof(DataPage), prefixLength, null));
                return this;
        }

        return NoopDisposable.Instance;
    }

    private void Claim(DbAddress addr)
    {
        if (!_claimed.Add(addr) || addr >= _reclaim.Next && addr < _reclaim.End)
        {
            PagesClaimedTwice++;
        }
    }

    /// <summary>Closes the scope of the bottom or data page that returned this visitor.</summary>
    void IDisposable.Dispose() => _parents.Pop();
}
