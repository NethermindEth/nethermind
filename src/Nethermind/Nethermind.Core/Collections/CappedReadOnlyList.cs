using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Core.Collections;

public readonly struct CappedReadOnlyList<T>: IReadOnlyList<T>
{
    private readonly IReadOnlyList<T> _baseReadonlyList;
    private readonly int _cappedLength;

    public CappedReadOnlyList(IReadOnlyList<T> baseReadonly, int cappedLength)
    {
        _baseReadonlyList = baseReadonly;
        _cappedLength = cappedLength;
    }

    public IEnumerator<T> GetEnumerator()
    {
        return _baseReadonlyList.Take(_cappedLength).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public int Count => Math.Min(_baseReadonlyList.Count, _cappedLength);

    public T this[int index]
    {
        get
        {
            if (index >= _cappedLength)
            {
                throw new IndexOutOfRangeException($"Index is {index} while count is {_cappedLength}");
            }

            return _baseReadonlyList[index];
        }
    }
}
