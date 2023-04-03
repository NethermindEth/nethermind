// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Reflection;
using Nethermind.Logging;

namespace Nethermind.Core.Collections;

/// <summary>
/// Helper class to be able to lock internals of <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
/// <typeparam name="TKey">The type of the keys in the dictionary.</typeparam>
/// <typeparam name="TValue">The type of the values in the dictionary.</typeparam>
public static class ConcurrentDictionaryLock<TKey, TValue> where TKey : notnull
{
    /// <summary>
    /// Delegate that is equivalent of <see cref="ConcurrentDictionary{TKey,TValue}.AcquireLocks"/>
    /// </summary>
    private delegate void AcquireAllLocks(ConcurrentDictionary<TKey, TValue> dictionary, ref int locksAcquired);

    /// <summary>
    /// Delegate that is equivalent of <see cref="ConcurrentDictionary{TKey,TValue}.ReleaseLocks"/>
    /// </summary>
    private delegate void ReleaseLocks(ConcurrentDictionary<TKey, TValue> dictionary, int fromInclusive, int toExclusive);

    /// <summary>
    /// Cached delegate of <see cref="ConcurrentDictionary{TKey,TValue}.AcquireLocks"/> to neglect reflection performance impact.
    /// </summary>
    private static readonly AcquireAllLocks _acquireAllLocksMethod;

    /// <summary>
    /// Cached delegate of <see cref="ConcurrentDictionary{TKey,TValue}.ReleaseLocks"/> to neglect reflection performance impact.
    /// </summary>
    private static readonly ReleaseLocks _releaseLocksMethod;

    /// <summary>
    /// Creates and caches delegates to private lock methods of <see cref="ConcurrentDictionary{TKey,TValue}"/>.
    /// </summary>
    /// <exception cref="NotSupportedException">Thrown when private members of <see cref="ConcurrentDictionary{TKey,TValue}"/> changed and we cannot create delegates.</exception>
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

    /// <summary>
    /// Acquires the internal lock on all the keys of <see cref="dictionary"/>.
    /// </summary>
    /// <param name="dictionary">Dictionary to lock.</param>
    /// <param name="logger"></param>
    /// <returns>Lock instance. To release the lock it needs to be <see cref="Lock.Dispose"/>d.</returns>
    public static Lock Acquire(ConcurrentDictionary<TKey, TValue> dictionary, ILogger logger) => new(dictionary, logger);

    /// <summary>
    /// Represents a lock on <see cref="ConcurrentDictionary{TKey,TValue}"/>.
    /// </summary>
    /// <remarks>
    /// This is a ref struct in order not to keep the locks longer than a method.
    /// You have to <see cref="Dispose"/> it to release the lock!
    /// </remarks>
    public readonly ref struct Lock
    {
        private readonly ConcurrentDictionary<TKey, TValue> _dictionary;
        private readonly ILogger _logger;
        private readonly int _locksAcquired = 0;

        internal Lock(ConcurrentDictionary<TKey, TValue> dictionary, ILogger logger)
        {
            _dictionary = dictionary;
            _logger = logger;
            _acquireAllLocksMethod(dictionary, ref _locksAcquired);
            if (_logger.IsInfo) _logger.Info($"Locked {typeof(Lock).FullName}.");
        }

        // Duck typing
        public void Dispose()
        {
            _releaseLocksMethod(_dictionary, 0, _locksAcquired);
            if (_logger.IsInfo) _logger.Info($"Unlocked {typeof(Lock).FullName}.");
        }
    }
}

public static class ConcurrentDictionaryExtensions
{
    /// <summary>
    /// Acquires the internal lock on all the keys of <see cref="dictionary"/>.
    /// </summary>
    /// <param name="dictionary">Dictionary to lock.</param>
    /// <param name="logger"></param>
    /// <returns>Lock instance. To release the lock it needs to be <see cref="ConcurrentDictionaryLock{TKey,TValue}.Lock.Dispose"/>d.</returns>
    public static ConcurrentDictionaryLock<TKey, TValue>.Lock AcquireLock<TKey, TValue>(this ConcurrentDictionary<TKey, TValue> dictionary, ILogger logger)
        where TKey : notnull =>
        ConcurrentDictionaryLock<TKey, TValue>.Acquire(dictionary, logger);
}


