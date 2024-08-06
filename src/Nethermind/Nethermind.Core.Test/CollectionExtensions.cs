// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nethermind.Core.Test;

public static class CollectionExtensions
{
    public static TValue AddTo<TValue, TCollection>(this TValue value, ICollection<TCollection> collection)
        where TValue: TCollection
    {
        collection.Add(value);
        return value;
    }

    public static Task<TValue> AddResultTo<TValue, TCollection>(this Task<TValue> task, ICollection<TCollection> disposables)
        where TValue: TCollection
    {
        return task.ContinueWith(t =>
        {
            if (t is { IsCompletedSuccessfully: true, Result: { } value })
                value.AddTo(disposables);
            return t.Result;
        });
    }
}
