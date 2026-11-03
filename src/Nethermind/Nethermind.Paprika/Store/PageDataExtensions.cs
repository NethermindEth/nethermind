using System.Diagnostics;
using System.Runtime.CompilerServices;
using Paprika.Data;

namespace Paprika.Store;

public static class PageDataExtensions
{
    public static bool TryGet(this Page page, IPageResolver batch, scoped in NibblePath key,
        out ReadOnlySpan<byte> result)
    {
        return page.Header.PageType switch
        {
            PageType.DataPage => new DataPage(page).TryGet(batch, key, out result),
            PageType.StateRoot => new StateRootPage(page).TryGet(batch, key, out result),
            PageType.Bottom => new BottomPage(page).TryGet(batch, key, out result),
            PageType.ChildBottom => new ChildBottomPage(page).TryGet(batch, key, out result),
            _ => ThrowOnType(page.Header.PageType, out result)
        };
    }

    public static void Set(this Page page, in NibblePath key, in ReadOnlySpan<byte> data, IBatchContext batch)
    {
        Debug.Assert(batch.WasWritten(batch.GetAddress(page)));

        switch (page.Header.PageType)
        {
            case PageType.DataPage:
                new DataPage(page).Set(key, data, batch);
                break;
            case PageType.StateRoot:
                new StateRootPage(page).Set(key, data, batch);
                break;
            case PageType.Bottom:
                new BottomPage(page).Set(key, data, batch);
                break;
            case PageType.ChildBottom:
                new ChildBottomPage(page).Set(key, data, batch);
                break;
            default:
                ThrowOnType(page.Header.PageType, out _);
                break;
        }
    }

    public static void DeleteByPrefix(this Page page, in NibblePath prefix, IBatchContext batch)
    {
        Debug.Assert(batch.WasWritten(batch.GetAddress(page)));

        switch (page.Header.PageType)
        {
            case PageType.DataPage:
                new DataPage(page).DeleteByPrefix(prefix, batch);
                break;
            case PageType.StateRoot:
                new StateRootPage(page).DeleteByPrefix(prefix, batch);
                break;
            case PageType.Bottom:
                new BottomPage(page).DeleteByPrefix(prefix, batch);
                break;
            case PageType.ChildBottom:
                new ChildBottomPage(page).DeleteByPrefix(prefix, batch);
                break;
            default:
                ThrowOnType(page.Header.PageType, out _);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ThrowOnType(PageType type, out ReadOnlySpan<byte> result) =>
        throw new Exception($"Page type is not handled:{type}");
}
