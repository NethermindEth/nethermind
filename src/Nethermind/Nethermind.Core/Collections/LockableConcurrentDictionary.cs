using System;
using System.Collections.Concurrent;
using System.Reflection;

namespace Nethermind.Core.Collections;

public static class ConcurrentDictionaryLock<TKey, TValue> where TKey : notnull
{
    private delegate void AcquireAllLocks(ConcurrentDictionary<TKey, TValue> dictionary, ref int locksAcquired);
    private delegate void ReleaseLocks(ConcurrentDictionary<TKey, TValue> dictionary, int fromInclusive, int toExclusive);

    private static readonly AcquireAllLocks _acquireAllLocksMethod;
    private static readonly ReleaseLocks _releaseLocksMethod;

    static ConcurrentDictionaryLock()
    {
        TDelegate CreateDelegate<TType, TDelegate>(TType? target = default, string? methodName = null) where TDelegate : Delegate
        {
            Type type = typeof(TType);
            Type delegateType = typeof(TDelegate);
            methodName ??= delegateType.Name;
            MethodInfo? method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (method is not null)
            {
                Delegate? methodDelegate = Delegate.CreateDelegate(delegateType, target, method, false);
                if (methodDelegate is not null)
                {
                    return (TDelegate)methodDelegate;
                }
            }

            throw new NotSupportedException($"Cannot create delegate of type {delegateType.FullName} for method {methodName} in type {type.FullName}.");
        }

        _acquireAllLocksMethod = CreateDelegate<ConcurrentDictionary<TKey, TValue>, AcquireAllLocks>();
        _releaseLocksMethod = CreateDelegate<ConcurrentDictionary<TKey, TValue>, ReleaseLocks>();
    }

    public static Lock Acquire(ConcurrentDictionary<TKey, TValue> dictionary) => new(dictionary);

    public readonly ref struct Lock
    {
        private readonly ConcurrentDictionary<TKey, TValue>? _dictionary;
        private readonly int _locksAcquired = 0;

        public Lock(ConcurrentDictionary<TKey, TValue> dictionary)
        {
            _dictionary = dictionary;
            _acquireAllLocksMethod(dictionary, ref _locksAcquired);
        }

        public void Dispose()
        {
            if (_dictionary is not null)
            {
                _releaseLocksMethod(_dictionary, 0, _locksAcquired);
            }
        }
    }
}


