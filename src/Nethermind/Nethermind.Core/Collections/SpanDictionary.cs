// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;

namespace Nethermind.Core.Collections
{
    /// Adapted from .net source code.
    [DebuggerDisplay("Count = {Count}")]
    [Serializable]
    public class SpanDictionary<TKey, TValue> : IDictionary<TKey[], TValue>, IDictionary, IReadOnlyDictionary<TKey[], TValue>, ISerializable, IDeserializationCallback where TKey : notnull
    {
        // constants for serialization
        private const string VersionName = "Version"; // Do not rename (binary serialization)
        private const string HashSizeName = "HashSize"; // Do not rename (binary serialization). Must save buckets.Length
        private const string KeyValuePairsName = "KeyValuePairs"; // Do not rename (binary serialization)
        private const string ComparerName = "Comparer"; // Do not rename (binary serialization)

        private int[]? _buckets;
        private Entry[]? _entries;
        private ulong _fastModMultiplier;
        private int _count;
        private int _freeList;
        private int _freeCount;
        private int _version;
        private ISpanEqualityComparer<TKey> _comparer;
        private KeyCollection? _keys;
        private ValueCollection? _values;
        private const int StartOfFreeList = -3;

        public SpanDictionary(ISpanEqualityComparer<TKey> comparer) : this(0, comparer) { }

        public SpanDictionary(int capacity, ISpanEqualityComparer<TKey> comparer)
        {
            if (capacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            if (capacity > 0)
            {
                Initialize(capacity);
            }

            ArgumentNullException.ThrowIfNull(comparer);
            _comparer = comparer;
        }

        public SpanDictionary(IDictionary<TKey[], TValue> dictionary, ISpanEqualityComparer<TKey> comparer) :
            this(dictionary != null ? dictionary.Count : 0, comparer)
        {
            ArgumentNullException.ThrowIfNull(dictionary);
            AddRange(dictionary);
        }

        public SpanDictionary(IEnumerable<KeyValuePair<TKey[], TValue>> collection, ISpanEqualityComparer<TKey> comparer) :
            this((collection as ICollection<KeyValuePair<TKey[], TValue>>)?.Count ?? 0, comparer)
        {
            ArgumentNullException.ThrowIfNull(collection);
            AddRange(collection);
        }

        private void AddRange(IEnumerable<KeyValuePair<TKey[], TValue>> collection)
        {
            // It is likely that the passed-in dictionary is Dictionary<TKey[],TValue>. When this is the case,
            // avoid the enumerator allocation and overhead by looping through the entries array directly.
            // We only do this when dictionary is Dictionary<TKey[],TValue> and not a subclass, to maintain
            // back-compat with subclasses that may have overridden the enumerator behavior.
            if (collection.GetType() == typeof(SpanDictionary<TKey, TValue>))
            {
                SpanDictionary<TKey, TValue> source = (SpanDictionary<TKey, TValue>)collection;

                if (source.Count == 0)
                {
                    // Nothing to copy, all done
                    return;
                }

                // This is not currently a true .AddRange as it needs to be an initialized dictionary
                // of the correct size, and also an empty dictionary with no current entities (and no argument checks).
                Debug.Assert(source._entries is not null);
                Debug.Assert(_entries is not null);
                Debug.Assert(_entries.Length >= source.Count);
                Debug.Assert(_count == 0);

                Entry[] oldEntries = source._entries;
                if (source._comparer == _comparer)
                {
                    // If comparers are the same, we can copy _entries without rehashing.
                    CopyEntries(oldEntries, source._count);
                    return;
                }

                // Comparers differ need to rehash all the entires via Add
                int count = source._count;
                for (int i = 0; i < count; i++)
                {
                    // Only copy if an entry
                    if (oldEntries[i].next >= -1)
                    {
                        Add(oldEntries[i].key, oldEntries[i].value);
                    }
                }

                return;
            }

            // Fallback path for IEnumerable that isn't a non-subclassed Dictionary<TKey[],TValue>.
            foreach (KeyValuePair<TKey[], TValue> pair in collection)
            {
                Add(pair.Key, pair.Value);
            }
        }

        public ISpanEqualityComparer<TKey> Comparer => _comparer;

        public int Count => _count - _freeCount;

        public KeyCollection Keys => _keys ??= new KeyCollection(this);

        ICollection<TKey[]> IDictionary<TKey[], TValue>.Keys => Keys;

        IEnumerable<TKey[]> IReadOnlyDictionary<TKey[], TValue>.Keys => Keys;

        public ValueCollection Values => _values ??= new ValueCollection(this);

        ICollection<TValue> IDictionary<TKey[], TValue>.Values => Values;

        IEnumerable<TValue> IReadOnlyDictionary<TKey[], TValue>.Values => Values;

        public TValue this[ReadOnlySpan<TKey> key]
        {
            get
            {
                ref TValue value = ref FindValue(key);
                if (!Unsafe.IsNullRef(ref value))
                {
                    return value;
                }

                throw new KeyNotFoundException($"The given key was not present in the dictionary.");
            }
            set
            {
                bool modified = TryInsert(key, null, value, InsertionBehavior.OverwriteExisting);
                Debug.Assert(modified);
            }
        }

        public TValue this[TKey[] key]
        {
            get
            {
                ref TValue value = ref FindValue(key);
                if (!Unsafe.IsNullRef(ref value))
                {
                    return value;
                }

                throw new KeyNotFoundException($"The given key '{key}' was not present in the dictionary.");
            }
            set
            {
                bool modified = TryInsert(key, key, value, InsertionBehavior.OverwriteExisting);
                Debug.Assert(modified);
            }
        }

        public void Add(TKey[] key, TValue value)
        {
            bool modified = TryInsert(key, key, value, InsertionBehavior.ThrowOnExisting);
            Debug.Assert(modified); // If there was an existing key and the Add failed, an exception will already have been thrown.
        }

        void ICollection<KeyValuePair<TKey[], TValue>>.Add(KeyValuePair<TKey[], TValue> keyValuePair) =>
            Add(keyValuePair.Key, keyValuePair.Value);

        bool ICollection<KeyValuePair<TKey[], TValue>>.Contains(KeyValuePair<TKey[], TValue> keyValuePair)
        {
            ref TValue value = ref FindValue(keyValuePair.Key);
            if (!Unsafe.IsNullRef(ref value) && EqualityComparer<TValue>.Default.Equals(value, keyValuePair.Value))
            {
                return true;
            }

            return false;
        }

        bool ICollection<KeyValuePair<TKey[], TValue>>.Remove(KeyValuePair<TKey[], TValue> keyValuePair)
        {
            ref TValue value = ref FindValue(keyValuePair.Key);
            if (!Unsafe.IsNullRef(ref value) && EqualityComparer<TValue>.Default.Equals(value, keyValuePair.Value))
            {
                Remove(keyValuePair.Key);
                return true;
            }

            return false;
        }

        public void Clear()
        {
            int count = _count;
            if (count > 0)
            {
                Debug.Assert(_buckets != null, "_buckets should be non-null");
                Debug.Assert(_entries != null, "_entries should be non-null");

                Array.Clear(_buckets);

                _count = 0;
                _freeList = -1;
                _freeCount = 0;
                Array.Clear(_entries, 0, count);
            }
        }

        public bool ContainsKey(TKey[] key) =>
            ContainsKey(key.AsSpan());

        public bool ContainsKey(ReadOnlySpan<TKey> key) =>
            !Unsafe.IsNullRef(ref FindValue(key));

        public bool ContainsValue(TValue value)
        {
            Entry[]? entries = _entries;
            if (value == null)
            {
                for (int i = 0; i < _count; i++)
                {
                    if (entries![i].next >= -1 && entries[i].value == null)
                    {
                        return true;
                    }
                }
            }
            else if (typeof(TValue).IsValueType)
            {
                // ValueType: Devirtualize with EqualityComparer<TValue>.Default intrinsic
                for (int i = 0; i < _count; i++)
                {
                    if (entries![i].next >= -1 && EqualityComparer<TValue>.Default.Equals(entries[i].value, value))
                    {
                        return true;
                    }
                }
            }
            else
            {
                // Object type: Shared Generic, EqualityComparer<TValue>.Default won't devirtualize
                // https://github.com/dotnet/runtime/issues/10050
                // So cache in a local rather than get EqualityComparer per loop iteration
                EqualityComparer<TValue> defaultComparer = EqualityComparer<TValue>.Default;
                for (int i = 0; i < _count; i++)
                {
                    if (entries![i].next >= -1 && defaultComparer.Equals(entries[i].value, value))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void CopyTo(KeyValuePair<TKey[], TValue>[] array, int index)
        {
            ArgumentNullException.ThrowIfNull(array);

            if ((uint)index > (uint)array.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "Non-negative number required.");
            }

            if (array.Length - index < Count)
            {
                throw new ArgumentException("Destination array is not long enough to copy all the items in the collection. Check array index and length.", nameof(array));
            }

            int count = _count;
            Entry[]? entries = _entries;
            for (int i = 0; i < count; i++)
            {
                if (entries![i].next >= -1)
                {
                    array[index++] = new KeyValuePair<TKey[], TValue>(entries[i].key, entries[i].value);
                }
            }
        }

        public Enumerator GetEnumerator() => new Enumerator(this, Enumerator.KeyValuePair);

        IEnumerator<KeyValuePair<TKey[], TValue>> IEnumerable<KeyValuePair<TKey[], TValue>>.GetEnumerator() =>
            new Enumerator(this, Enumerator.KeyValuePair);

        public virtual void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            ArgumentNullException.ThrowIfNull(info);

            info.AddValue(VersionName, _version);
            info.AddValue(ComparerName, Comparer, typeof(ISpanEqualityComparer<TKey>));
            info.AddValue(HashSizeName, _buckets == null ? 0 : _buckets.Length); // This is the length of the bucket array

            if (_buckets != null)
            {
                var array = new KeyValuePair<TKey[], TValue>[Count];
                CopyTo(array, 0);
                info.AddValue(KeyValuePairsName, array, typeof(KeyValuePair<TKey[], TValue>[]));
            }
        }

        internal ref TValue FindValue(ReadOnlySpan<TKey> key)
        {
            ref Entry entry = ref Unsafe.NullRef<Entry>();
            if (_buckets != null)
            {
                Debug.Assert(_entries != null, "expected entries to be != null");
                ISpanEqualityComparer<TKey> comparer = _comparer;
                uint hashCode = (uint)comparer.GetHashCode(key);
                int i = GetBucket(hashCode);
                Entry[]? entries = _entries;
                uint collisionCount = 0;
                i--; // Value in _buckets is 1-based; subtract 1 from i. We do it here so it fuses with the following conditional.
                do
                {
                    // Should be a while loop https://github.com/dotnet/runtime/issues/9422
                    // Test in if to drop range check for following array access
                    if ((uint)i >= (uint)entries.Length)
                    {
                        goto ReturnNotFound;
                    }

                    entry = ref entries[i];
                    if (entry.hashCode == hashCode && comparer.Equals(entry.key, key))
                    {
                        goto ReturnFound;
                    }

                    i = entry.next;

                    collisionCount++;
                } while (collisionCount <= (uint)entries.Length);

                // The chain of entries forms a loop; which means a concurrent update has happened.
                // Break out of the loop and throw, rather than looping forever.
                goto ConcurrentOperation;
            }

            goto ReturnNotFound;

ConcurrentOperation:
            throw new InvalidOperationException("Concurrent operations not supported");
ReturnFound:
            ref TValue value = ref entry.value;
Return:
            return ref value;
ReturnNotFound:
            value = ref Unsafe.NullRef<TValue>();
            goto Return;
        }

        private int Initialize(int capacity)
        {
            int size = HashHelpers.GetPrime(capacity);
            int[] buckets = new int[size];
            Entry[] entries = new Entry[size];

            // Assign member variables after both arrays allocated to guard against corruption from OOM if second fails
            _freeList = -1;
            _fastModMultiplier = HashHelpers.GetFastModMultiplier((uint)size);
            _buckets = buckets;
            _entries = entries;

            return size;
        }

        private bool TryInsert(ReadOnlySpan<TKey> key, TKey[]? keyArray, TValue value, InsertionBehavior behavior)
        {
            // NOTE: this method is mirrored in CollectionsMarshal.GetValueRefOrAddDefault below.
            // If you make any changes here, make sure to keep that version in sync as well.

            if (_buckets == null)
            {
                Initialize(0);
            }
            Debug.Assert(_buckets != null);

            Entry[]? entries = _entries;
            Debug.Assert(entries != null, "expected entries to be non-null");

            ISpanEqualityComparer<TKey> comparer = _comparer;
            uint hashCode = (uint)(comparer.GetHashCode(key));

            uint collisionCount = 0;
            ref int bucket = ref GetBucket(hashCode);
            int i = bucket - 1; // Value in _buckets is 1-based

            while (true)
            {
                // Should be a while loop https://github.com/dotnet/runtime/issues/9422
                // Test uint in if rather than loop condition to drop range check for following array access
                if ((uint)i >= (uint)entries.Length)
                {
                    break;
                }

                if (entries[i].hashCode == hashCode && comparer.Equals(entries[i].key, key))
                {
                    if (behavior == InsertionBehavior.OverwriteExisting)
                    {
                        entries[i].value = value;
                        return true;
                    }

                    if (behavior == InsertionBehavior.ThrowOnExisting)
                    {
                        throw new ArgumentException("An item with the same key has already been added.", nameof(key));
                    }

                    return false;
                }

                i = entries[i].next;

                collisionCount++;
                if (collisionCount > (uint)entries.Length)
                {
                    // The chain of entries forms a loop; which means a concurrent update has happened.
                    // Break out of the loop and throw, rather than looping forever.
                    throw new InvalidOperationException("Concurrent operations not supported");
                }
            }

            int index;
            if (_freeCount > 0)
            {
                index = _freeList;
                Debug.Assert((StartOfFreeList - entries[_freeList].next) >= -1, "shouldn't overflow because `next` cannot underflow");
                _freeList = StartOfFreeList - entries[_freeList].next;
                _freeCount--;
            }
            else
            {
                int count = _count;
                if (count == entries.Length)
                {
                    Resize();
                    bucket = ref GetBucket(hashCode);
                }
                index = count;
                _count = count + 1;
                entries = _entries;
            }

            ref Entry entry = ref entries![index];
            entry.hashCode = hashCode;
            entry.next = bucket - 1; // Value in _buckets is 1-based
            entry.key = keyArray ?? key.ToArray();
            entry.value = value;
            bucket = index + 1; // Value in _buckets is 1-based
            _version++;

            return true;
        }

        /// <summary>
        /// A helper class containing APIs exposed through <see cref="CollectionsMarshal"/>.
        /// These methods are relatively niche and only used in specific scenarios, so adding them in a separate type avoids
        /// the additional overhead on each <see cref="SpanDictionary{TKey,TValue}"/> instantiation, especially in AOT scenarios.
        /// </summary>
        internal static class CollectionsMarshalHelper
        {
            /// <inheritdoc cref="Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault{TKey, TValue}(SpanDictionary{TKey,TValue}, TKey[], out bool)"/>
            public static ref TValue? GetValueRefOrAddDefault(SpanDictionary<TKey, TValue> dictionary, TKey[] key, out bool exists)
            {
                // NOTE: this method is mirrored by Dictionary<TKey[], TValue>.TryInsert above.
                // If you make any changes here, make sure to keep that version in sync as well.

                ArgumentNullException.ThrowIfNull(key);

                if (dictionary._buckets == null)
                {
                    dictionary.Initialize(0);
                }
                Debug.Assert(dictionary._buckets != null);

                Entry[]? entries = dictionary._entries;
                Debug.Assert(entries != null, "expected entries to be non-null");

                ISpanEqualityComparer<TKey>? comparer = dictionary._comparer;
                uint hashCode = (uint)(comparer?.GetHashCode(key) ?? key.GetHashCode());

                uint collisionCount = 0;
                ref int bucket = ref dictionary.GetBucket(hashCode);
                int i = bucket - 1; // Value in _buckets is 1-based

                if (comparer == null)
                {
                    if (typeof(TKey[]).IsValueType)
                    {
                        // ValueType: Devirtualize with EqualityComparer<TValue>.Default intrinsic
                        while (true)
                        {
                            // Should be a while loop https://github.com/dotnet/runtime/issues/9422
                            // Test uint in if rather than loop condition to drop range check for following array access
                            if ((uint)i >= (uint)entries.Length)
                            {
                                break;
                            }

                            if (entries[i].hashCode == hashCode && EqualityComparer<TKey[]>.Default.Equals(entries[i].key, key))
                            {
                                exists = true;

                                return ref entries[i].value!;
                            }

                            i = entries[i].next;

                            collisionCount++;
                            if (collisionCount > (uint)entries.Length)
                            {
                                // The chain of entries forms a loop; which means a concurrent update has happened.
                                // Break out of the loop and throw, rather than looping forever.
                                throw new InvalidOperationException("Concurrent operations not supported");
                            }
                        }
                    }
                    else
                    {
                        // Object type: Shared Generic, EqualityComparer<TValue>.Default won't devirtualize
                        // https://github.com/dotnet/runtime/issues/10050
                        // So cache in a local rather than get EqualityComparer per loop iteration
                        EqualityComparer<TKey[]> defaultComparer = EqualityComparer<TKey[]>.Default;
                        while (true)
                        {
                            // Should be a while loop https://github.com/dotnet/runtime/issues/9422
                            // Test uint in if rather than loop condition to drop range check for following array access
                            if ((uint)i >= (uint)entries.Length)
                            {
                                break;
                            }

                            if (entries[i].hashCode == hashCode && defaultComparer.Equals(entries[i].key, key))
                            {
                                exists = true;

                                return ref entries[i].value!;
                            }

                            i = entries[i].next;

                            collisionCount++;
                            if (collisionCount > (uint)entries.Length)
                            {
                                // The chain of entries forms a loop; which means a concurrent update has happened.
                                // Break out of the loop and throw, rather than looping forever.
                                throw new InvalidOperationException("Concurrent operations not supported");
                            }
                        }
                    }
                }
                else
                {
                    while (true)
                    {
                        // Should be a while loop https://github.com/dotnet/runtime/issues/9422
                        // Test uint in if rather than loop condition to drop range check for following array access
                        if ((uint)i >= (uint)entries.Length)
                        {
                            break;
                        }

                        if (entries[i].hashCode == hashCode && comparer.Equals(entries[i].key, key))
                        {
                            exists = true;

                            return ref entries[i].value!;
                        }

                        i = entries[i].next;

                        collisionCount++;
                        if (collisionCount > (uint)entries.Length)
                        {
                            // The chain of entries forms a loop; which means a concurrent update has happened.
                            // Break out of the loop and throw, rather than looping forever.
                            throw new InvalidOperationException("Concurrent operations not supported");
                        }
                    }
                }

                int index;
                if (dictionary._freeCount > 0)
                {
                    index = dictionary._freeList;
                    Debug.Assert((StartOfFreeList - entries[dictionary._freeList].next) >= -1, "shouldn't overflow because `next` cannot underflow");
                    dictionary._freeList = StartOfFreeList - entries[dictionary._freeList].next;
                    dictionary._freeCount--;
                }
                else
                {
                    int count = dictionary._count;
                    if (count == entries.Length)
                    {
                        dictionary.Resize();
                        bucket = ref dictionary.GetBucket(hashCode);
                    }
                    index = count;
                    dictionary._count = count + 1;
                    entries = dictionary._entries;
                }

                ref Entry entry = ref entries![index];
                entry.hashCode = hashCode;
                entry.next = bucket - 1; // Value in _buckets is 1-based
                entry.key = key;
                entry.value = default!;
                bucket = index + 1; // Value in _buckets is 1-based
                dictionary._version++;

                exists = false;

                return ref entry.value!;
            }
        }

        public virtual void OnDeserialization(object? sender)
        {
            HashHelpers.SerializationInfoTable.TryGetValue(this, out SerializationInfo? siInfo);

            if (siInfo == null)
            {
                // We can return immediately if this function is called twice.
                // Note we remove the serialization info from the table at the end of this method.
                return;
            }

            int realVersion = siInfo.GetInt32(VersionName);
            int hashsize = siInfo.GetInt32(HashSizeName);
            _comparer = (ISpanEqualityComparer<TKey>)siInfo.GetValue(ComparerName, typeof(ISpanEqualityComparer<TKey>))!; // When serialized if comparer is null, we use the default.

            if (hashsize != 0)
            {
                Initialize(hashsize);

                KeyValuePair<TKey[], TValue>[]? array = (KeyValuePair<TKey[], TValue>[]?)
                    siInfo.GetValue(KeyValuePairsName, typeof(KeyValuePair<TKey[], TValue>[]));

                if (array == null)
                {
                    throw new SerializationException("The Keys for this Hashtable are missing.");
                }

                for (int i = 0; i < array.Length; i++)
                {
                    if (array[i].Key == null)
                    {
                        throw new SerializationException("One of the serialized keys is null.");
                    }

                    Add(array[i].Key, array[i].Value);
                }
            }
            else
            {
                _buckets = null;
            }

            _version = realVersion;
            HashHelpers.SerializationInfoTable.Remove(this);
        }

        private void Resize() => Resize(HashHelpers.ExpandPrime(_count), false);

        private void Resize(int newSize, bool forceNewHashCodes)
        {
            // Value types never rehash
            Debug.Assert(!forceNewHashCodes || !typeof(TKey[]).IsValueType);
            Debug.Assert(_entries != null, "_entries should be non-null");
            Debug.Assert(newSize >= _entries.Length);

            Entry[] entries = new Entry[newSize];

            int count = _count;
            Array.Copy(_entries, entries, count);

            // Assign member variables after both arrays allocated to guard against corruption from OOM if second fails
            _buckets = new int[newSize];
            _fastModMultiplier = HashHelpers.GetFastModMultiplier((uint)newSize);
            for (int i = 0; i < count; i++)
            {
                if (entries[i].next >= -1)
                {
                    ref int bucket = ref GetBucket(entries[i].hashCode);
                    entries[i].next = bucket - 1; // Value in _buckets is 1-based
                    bucket = i + 1;
                }
            }

            _entries = entries;
        }

        public bool Remove(TKey[] key)
        {
            ArgumentNullException.ThrowIfNull(key);

            return Remove(key.AsSpan());
        }

        public bool Remove(ReadOnlySpan<TKey> key)
        {
            // The overload Remove(TKey[] key, out TValue value) is a copy of this method with one additional
            // statement to copy the value for entry being removed into the output parameter.
            // Code has been intentionally duplicated for performance reasons.

            if (_buckets != null)
            {
                Debug.Assert(_entries != null, "entries should be non-null");
                uint collisionCount = 0;
                uint hashCode = (uint)(_comparer.GetHashCode(key));
                ref int bucket = ref GetBucket(hashCode);
                Entry[]? entries = _entries;
                int last = -1;
                int i = bucket - 1; // Value in buckets is 1-based
                while (i >= 0)
                {
                    ref Entry entry = ref entries[i];

                    if (entry.hashCode == hashCode && _comparer!.Equals(entry.key, key))
                    {
                        if (last < 0)
                        {
                            bucket = entry.next + 1; // Value in buckets is 1-based
                        }
                        else
                        {
                            entries[last].next = entry.next;
                        }

                        Debug.Assert((StartOfFreeList - _freeList) < 0, "shouldn't underflow because max hashtable length is MaxPrimeArrayLength = 0x7FEFFFFD(2146435069) _freelist underflow threshold 2147483646");
                        entry.next = StartOfFreeList - _freeList;

                        if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey[]>())
                        {
                            entry.key = default!;
                        }

                        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
                        {
                            entry.value = default!;
                        }

                        _freeList = i;
                        _freeCount++;
                        return true;
                    }

                    last = i;
                    i = entry.next;

                    collisionCount++;
                    if (collisionCount > (uint)entries.Length)
                    {
                        // The chain of entries forms a loop; which means a concurrent update has happened.
                        // Break out of the loop and throw, rather than looping forever.
                        throw new InvalidOperationException("Concurrent operations not supported");
                    }
                }
            }
            return false;
        }

        public bool Remove(TKey[] key, [MaybeNullWhen(false)] out TValue value)
        {
            // This overload is a copy of the overload Remove(TKey[] key) with one additional
            // statement to copy the value for entry being removed into the output parameter.
            // Code has been intentionally duplicated for performance reasons.

            ArgumentNullException.ThrowIfNull(key);

            if (_buckets != null)
            {
                Debug.Assert(_entries != null, "entries should be non-null");
                uint collisionCount = 0;
                uint hashCode = (uint)(_comparer?.GetHashCode(key) ?? key.GetHashCode());
                ref int bucket = ref GetBucket(hashCode);
                Entry[]? entries = _entries;
                int last = -1;
                int i = bucket - 1; // Value in buckets is 1-based
                while (i >= 0)
                {
                    ref Entry entry = ref entries[i];

                    if (entry.hashCode == hashCode && (_comparer?.Equals(entry.key, key) ?? EqualityComparer<TKey[]>.Default.Equals(entry.key, key)))
                    {
                        if (last < 0)
                        {
                            bucket = entry.next + 1; // Value in buckets is 1-based
                        }
                        else
                        {
                            entries[last].next = entry.next;
                        }

                        value = entry.value;

                        Debug.Assert((StartOfFreeList - _freeList) < 0, "shouldn't underflow because max hashtable length is MaxPrimeArrayLength = 0x7FEFFFFD(2146435069) _freelist underflow threshold 2147483646");
                        entry.next = StartOfFreeList - _freeList;

                        if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey[]>())
                        {
                            entry.key = default!;
                        }

                        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
                        {
                            entry.value = default!;
                        }

                        _freeList = i;
                        _freeCount++;
                        return true;
                    }

                    last = i;
                    i = entry.next;

                    collisionCount++;
                    if (collisionCount > (uint)entries.Length)
                    {
                        // The chain of entries forms a loop; which means a concurrent update has happened.
                        // Break out of the loop and throw, rather than looping forever.
                        throw new InvalidOperationException("Concurrent operations not supported");
                    }
                }
            }

            value = default;
            return false;
        }

        public bool TryGetValue(TKey[] key, [MaybeNullWhen(false)] out TValue value) =>
            TryGetValue(key.AsSpan(), out value);

        public bool TryGetValue(ReadOnlySpan<TKey> key, [MaybeNullWhen(false)] out TValue value)
        {
            ref TValue valRef = ref FindValue(key);
            if (!Unsafe.IsNullRef(ref valRef))
            {
                value = valRef;
                return true;
            }

            value = default;
            return false;
        }

        public bool TryAdd(ReadOnlySpan<TKey> key, TValue value) =>
            TryInsert(key, null, value, InsertionBehavior.None);

        public bool TryAdd(TKey[] key, TValue value) =>
            TryInsert(key, key, value, InsertionBehavior.None);

        bool ICollection<KeyValuePair<TKey[], TValue>>.IsReadOnly => false;

        void ICollection<KeyValuePair<TKey[], TValue>>.CopyTo(KeyValuePair<TKey[], TValue>[] array, int index) =>
            CopyTo(array, index);

        void ICollection.CopyTo(Array array, int index)
        {
            ArgumentNullException.ThrowIfNull(array);

            if (array.Rank != 1)
            {
                throw new ArgumentException("Only single dimensional arrays are supported for the requested action.");
            }

            if (array.GetLowerBound(0) != 0)
            {
                throw new ArgumentException("The lower bound of target array must be zero.");
            }

            if ((uint)index > (uint)array.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "Non-negative number required.");
            }

            if (array.Length - index < Count)
            {
                throw new ArgumentException("Destination array is not long enough to copy all the items in the collection. Check array index and length.", nameof(array));
            }

            if (array is KeyValuePair<TKey[], TValue>[] pairs)
            {
                CopyTo(pairs, index);
            }
            else if (array is DictionaryEntry[] dictEntryArray)
            {
                Entry[]? entries = _entries;
                for (int i = 0; i < _count; i++)
                {
                    if (entries![i].next >= -1)
                    {
                        dictEntryArray[index++] = new DictionaryEntry(entries[i].key, entries[i].value);
                    }
                }
            }
            else
            {
                object[]? objects = array as object[];
                if (objects == null)
                {
                    throw new ArgumentException("Target array type is not compatible with the type of items in the collection.");
                }

                try
                {
                    int count = _count;
                    Entry[]? entries = _entries;
                    for (int i = 0; i < count; i++)
                    {
                        if (entries![i].next >= -1)
                        {
                            objects[index++] = new KeyValuePair<TKey[], TValue>(entries[i].key, entries[i].value);
                        }
                    }
                }
                catch (ArrayTypeMismatchException)
                {
                    throw new ArgumentException("Target array type is not compatible with the type of items in the collection.");
                }
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => new Enumerator(this, Enumerator.KeyValuePair);

        /// <summary>
        /// Ensures that the dictionary can hold up to 'capacity' entries without any further expansion of its backing storage
        /// </summary>
        public int EnsureCapacity(int capacity)
        {
            if (capacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            int currentCapacity = _entries == null ? 0 : _entries.Length;
            if (currentCapacity >= capacity)
            {
                return currentCapacity;
            }

            _version++;

            if (_buckets == null)
            {
                return Initialize(capacity);
            }

            int newSize = HashHelpers.GetPrime(capacity);
            Resize(newSize, forceNewHashCodes: false);
            return newSize;
        }

        /// <summary>
        /// Sets the capacity of this dictionary to what it would be if it had been originally initialized with all its entries
        /// </summary>
        /// <remarks>
        /// This method can be used to minimize the memory overhead
        /// once it is known that no new elements will be added.
        ///
        /// To allocate minimum size storage array, execute the following statements:
        ///
        /// dictionary.Clear();
        /// dictionary.TrimExcess();
        /// </remarks>
        public void TrimExcess() => TrimExcess(Count);

        /// <summary>
        /// Sets the capacity of this dictionary to hold up 'capacity' entries without any further expansion of its backing storage
        /// </summary>
        /// <remarks>
        /// This method can be used to minimize the memory overhead
        /// once it is known that no new elements will be added.
        /// </remarks>
        public void TrimExcess(int capacity)
        {
            if (capacity < Count)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            int newSize = HashHelpers.GetPrime(capacity);
            Entry[]? oldEntries = _entries;
            int currentCapacity = oldEntries?.Length ?? 0;
            if (newSize >= currentCapacity)
            {
                return;
            }

            int oldCount = _count;
            _version++;
            Initialize(newSize);

            Debug.Assert(oldEntries is not null);

            CopyEntries(oldEntries, oldCount);
        }

        private void CopyEntries(Entry[] entries, int count)
        {
            Debug.Assert(_entries is not null);

            Entry[] newEntries = _entries;
            int newCount = 0;
            for (int i = 0; i < count; i++)
            {
                uint hashCode = entries[i].hashCode;
                if (entries[i].next >= -1)
                {
                    ref Entry entry = ref newEntries[newCount];
                    entry = entries[i];
                    ref int bucket = ref GetBucket(hashCode);
                    entry.next = bucket - 1; // Value in _buckets is 1-based
                    bucket = newCount + 1;
                    newCount++;
                }
            }

            _count = newCount;
            _freeCount = 0;
        }

        bool ICollection.IsSynchronized => false;

        object ICollection.SyncRoot => this;

        bool IDictionary.IsFixedSize => false;

        bool IDictionary.IsReadOnly => false;

        ICollection IDictionary.Keys => Keys;

        ICollection IDictionary.Values => Values;

        object? IDictionary.this[object key]
        {
            get
            {
                if (IsCompatibleKey(key))
                {
                    ref TValue value = ref FindValue((TKey[])key);
                    if (!Unsafe.IsNullRef(ref value))
                    {
                        return value;
                    }
                }

                return null;
            }
            set
            {
                ArgumentNullException.ThrowIfNull(key);
                ThrowHelper.IfNullAndNullsAreIllegalThenThrow<TValue>(value, nameof(value));

                try
                {
                    TKey[] tempKey = (TKey[])key;
                    try
                    {
                        this[tempKey] = (TValue)value!;
                    }
                    catch (InvalidCastException)
                    {
                        throw new ArgumentException($"The value \"{value}\" is not of type \"{typeof(TValue)}\" and cannot be used in this generic collection.", nameof(value));
                    }
                }
                catch (InvalidCastException)
                {
                    throw new ArgumentException($"The value \"{key}\" is not of type \"{typeof(TKey[])}\" and cannot be used in this generic collection.", nameof(key));
                }
            }
        }

        private static bool IsCompatibleKey(object key)
        {
            ArgumentNullException.ThrowIfNull(key);
            return key is TKey[];
        }

        void IDictionary.Add(object key, object? value)
        {
            ArgumentNullException.ThrowIfNull(key);
            ThrowHelper.IfNullAndNullsAreIllegalThenThrow<TValue>(value, nameof(value));

            try
            {
                TKey[] tempKey = (TKey[])key;

                try
                {
                    Add(tempKey, (TValue)value!);
                }
                catch (InvalidCastException)
                {
                    throw new ArgumentException($"The value \"{value}\" is not of type \"{typeof(TValue)}\" and cannot be used in this generic collection.", nameof(value));
                }
            }
            catch (InvalidCastException)
            {
                throw new ArgumentException($"The value \"{key}\" is not of type \"{typeof(TKey[])}\" and cannot be used in this generic collection.", nameof(key));
            }
        }

        bool IDictionary.Contains(object key)
        {
            if (IsCompatibleKey(key))
            {
                return ContainsKey((TKey[])key);
            }

            return false;
        }

        IDictionaryEnumerator IDictionary.GetEnumerator() => new Enumerator(this, Enumerator.DictEntry);

        void IDictionary.Remove(object key)
        {
            if (IsCompatibleKey(key))
            {
                Remove((TKey[])key);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ref int GetBucket(uint hashCode)
        {
            int[] buckets = _buckets!;
            return ref buckets[HashHelpers.FastMod(hashCode, (uint)buckets.Length, _fastModMultiplier)];
        }

        private struct Entry
        {
            public uint hashCode;
            /// <summary>
            /// 0-based index of next entry in chain: -1 means end of chain
            /// also encodes whether this entry _itself_ is part of the free list by changing sign and subtracting 3,
            /// so -2 means end of free list, -3 means index 0 but on free list, -4 means index 1 but on free list, etc.
            /// </summary>
            public int next;
            public TKey[] key;     // Key of entry
            public TValue value; // Value of entry
        }

        public struct Enumerator : IEnumerator<KeyValuePair<TKey[], TValue>>, IDictionaryEnumerator
        {
            private readonly SpanDictionary<TKey, TValue> _spanDictionary;
            private readonly int _version;
            private int _index;
            private KeyValuePair<TKey[], TValue> _current;
            private readonly int _getEnumeratorRetType;  // What should Enumerator.Current return?

            internal const int DictEntry = 1;
            internal const int KeyValuePair = 2;

            internal Enumerator(SpanDictionary<TKey, TValue> dictionary, int getEnumeratorRetType)
            {
                _spanDictionary = dictionary;
                _version = dictionary._version;
                _index = 0;
                _getEnumeratorRetType = getEnumeratorRetType;
                _current = default;
            }

            public bool MoveNext()
            {
                if (_version != _spanDictionary._version)
                {
                    throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");
                }

                // Use unsigned comparison since we set index to dictionary.count+1 when the enumeration ends.
                // dictionary.count+1 could be negative if dictionary.count is int.MaxValue
                while ((uint)_index < (uint)_spanDictionary._count)
                {
                    ref Entry entry = ref _spanDictionary._entries![_index++];

                    if (entry.next >= -1)
                    {
                        _current = new KeyValuePair<TKey[], TValue>(entry.key, entry.value);
                        return true;
                    }
                }

                _index = _spanDictionary._count + 1;
                _current = default;
                return false;
            }

            public KeyValuePair<TKey[], TValue> Current => _current;

            public void Dispose() { }

            object? IEnumerator.Current
            {
                get
                {
                    if (_index == 0 || (_index == _spanDictionary._count + 1))
                    {
                        throw new InvalidOperationException("Enumeration has either not started or has already finished.");
                    }

                    if (_getEnumeratorRetType == DictEntry)
                    {
                        return new DictionaryEntry(_current.Key, _current.Value);
                    }

                    return new KeyValuePair<TKey[], TValue>(_current.Key, _current.Value);
                }
            }

            void IEnumerator.Reset()
            {
                if (_version != _spanDictionary._version)
                {
                    throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");
                }

                _index = 0;
                _current = default;
            }

            DictionaryEntry IDictionaryEnumerator.Entry
            {
                get
                {
                    if (_index == 0 || (_index == _spanDictionary._count + 1))
                    {
                        throw new InvalidOperationException("Enumeration has either not started or has already finished.");
                    }

                    return new DictionaryEntry(_current.Key, _current.Value);
                }
            }

            object IDictionaryEnumerator.Key
            {
                get
                {
                    if (_index == 0 || (_index == _spanDictionary._count + 1))
                    {
                        throw new InvalidOperationException("Enumeration has either not started or has already finished.");
                    }

                    return _current.Key;
                }
            }

            object? IDictionaryEnumerator.Value
            {
                get
                {
                    if (_index == 0 || (_index == _spanDictionary._count + 1))
                    {
                        throw new InvalidOperationException("Enumeration has either not started or has already finished.");
                    }

                    return _current.Value;
                }
            }
        }

        [DebuggerDisplay("Count = {Count}")]
        public sealed class KeyCollection : ICollection<TKey[]>, ICollection, IReadOnlyCollection<TKey[]>
        {
            private readonly SpanDictionary<TKey, TValue> _spanDictionary;

            public KeyCollection(SpanDictionary<TKey, TValue> dictionary)
            {
                ArgumentNullException.ThrowIfNull(dictionary);

                _spanDictionary = dictionary;
            }

            public Enumerator GetEnumerator() => new Enumerator(_spanDictionary);

            public void CopyTo(TKey[][] array, int index)
            {
                ArgumentNullException.ThrowIfNull(array);

                if (index < 0 || index > array.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(index), "Non-negative number required.");
                }

                if (array.Length - index < _spanDictionary.Count)
                {
                    // throw new ArgumentException("Destination array is not long enough to copy all the items in the collection. Check array index and length.", nameof(array));
                }

                int count = _spanDictionary._count;
                Entry[]? entries = _spanDictionary._entries;
                for (int i = 0; i < count; i++)
                {
                    if (entries![i].next >= -1) array[index++] = entries[i].key;
                }
            }

            public int Count => _spanDictionary.Count;

            bool ICollection<TKey[]>.IsReadOnly => true;

            void ICollection<TKey[]>.Add(TKey[] item) =>
                throw new NotSupportedException("Mutating a key collection derived from a dictionary is not allowed.");

            void ICollection<TKey[]>.Clear() =>
                throw new NotSupportedException("Mutating a key collection derived from a dictionary is not allowed.");

            bool ICollection<TKey[]>.Contains(TKey[] item) =>
                _spanDictionary.ContainsKey(item);

            bool ICollection<TKey[]>.Remove(TKey[] item) =>
                throw new NotSupportedException("Mutating a key collection derived from a dictionary is not allowed.");


            IEnumerator<TKey[]> IEnumerable<TKey[]>.GetEnumerator() => new Enumerator(_spanDictionary);

            IEnumerator IEnumerable.GetEnumerator() => new Enumerator(_spanDictionary);

            void ICollection.CopyTo(Array array, int index)
            {
                ArgumentNullException.ThrowIfNull(array);

                if (array.Rank != 1)
                {
                    throw new ArgumentException("Only single dimensional arrays are supported for the requested action.");
                }

                if (array.GetLowerBound(0) != 0)
                {
                    throw new ArgumentException("The lower bound of target array must be zero.");
                }

                if ((uint)index > (uint)array.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(index), "Non-negative number required.");
                }

                if (array.Length - index < _spanDictionary.Count)
                {
                    // throw new ArgumentException("Destination array is not long enough to copy all the items in the collection. Check array index and length.", nameof(array));
                }

                if (array is TKey[][] keys)
                {
                    CopyTo(keys, index);
                }
                else
                {
                    object[]? objects = array as object[];
                    if (objects == null)
                    {
                        throw new ArgumentException("Target array type is not compatible with the type of items in the collection.");
                    }

                    int count = _spanDictionary._count;
                    Entry[]? entries = _spanDictionary._entries;
                    try
                    {
                        for (int i = 0; i < count; i++)
                        {
                            if (entries![i].next >= -1) objects[index++] = entries[i].key;
                        }
                    }
                    catch (ArrayTypeMismatchException)
                    {
                        throw new ArgumentException("Target array type is not compatible with the type of items in the collection.");
                    }
                }
            }

            bool ICollection.IsSynchronized => false;

            object ICollection.SyncRoot => ((ICollection)_spanDictionary).SyncRoot;

            public struct Enumerator : IEnumerator<TKey[]>, IEnumerator
            {
                private readonly SpanDictionary<TKey, TValue> _spanDictionary;
                private int _index;
                private readonly int _version;
                private TKey[]? _currentKey;

                internal Enumerator(SpanDictionary<TKey, TValue> dictionary)
                {
                    _spanDictionary = dictionary;
                    _version = dictionary._version;
                    _index = 0;
                    _currentKey = default;
                }

                public void Dispose() { }

                public bool MoveNext()
                {
                    if (_version != _spanDictionary._version)
                    {
                        throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");
                    }

                    while ((uint)_index < (uint)_spanDictionary._count)
                    {
                        ref Entry entry = ref _spanDictionary._entries![_index++];

                        if (entry.next >= -1)
                        {
                            _currentKey = entry.key;
                            return true;
                        }
                    }

                    _index = _spanDictionary._count + 1;
                    _currentKey = default;
                    return false;
                }

                public TKey[] Current => _currentKey!;

                object? IEnumerator.Current
                {
                    get
                    {
                        if (_index == 0 || (_index == _spanDictionary._count + 1))
                        {
                            throw new InvalidOperationException("Enumeration has either not started or has already finished.");
                        }

                        return _currentKey;
                    }
                }

                void IEnumerator.Reset()
                {
                    if (_version != _spanDictionary._version)
                    {
                        throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");
                    }

                    _index = 0;
                    _currentKey = default;
                }
            }
        }

        [DebuggerDisplay("Count = {Count}")]
        public sealed class ValueCollection : ICollection<TValue>, ICollection, IReadOnlyCollection<TValue>
        {
            private readonly SpanDictionary<TKey, TValue> _spanDictionary;

            public ValueCollection(SpanDictionary<TKey, TValue> dictionary)
            {
                ArgumentNullException.ThrowIfNull(dictionary);
                _spanDictionary = dictionary;
            }

            public Enumerator GetEnumerator() => new Enumerator(_spanDictionary);

            public void CopyTo(TValue[] array, int index)
            {
                ArgumentNullException.ThrowIfNull(array);

                if ((uint)index > array.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(index), "Non-negative number required.");
                }

                if (array.Length - index < _spanDictionary.Count)
                {
                    // throw new ArgumentException("Destination array is not long enough to copy all the items in the collection. Check array index and length.", nameof(array));
                }

                int count = _spanDictionary._count;
                Entry[]? entries = _spanDictionary._entries;
                for (int i = 0; i < count; i++)
                {
                    if (entries![i].next >= -1) array[index++] = entries[i].value;
                }
            }

            public int Count => _spanDictionary.Count;

            bool ICollection<TValue>.IsReadOnly => true;

            void ICollection<TValue>.Add(TValue item) =>
                throw new NotSupportedException("Mutating a value collection derived from a dictionary is not allowed.");

            bool ICollection<TValue>.Remove(TValue item) =>
                throw new NotSupportedException("Mutating a value collection derived from a dictionary is not allowed.");

            void ICollection<TValue>.Clear() =>
                throw new NotSupportedException("Mutating a value collection derived from a dictionary is not allowed.");

            bool ICollection<TValue>.Contains(TValue item) => _spanDictionary.ContainsValue(item);

            IEnumerator<TValue> IEnumerable<TValue>.GetEnumerator() => new Enumerator(_spanDictionary);

            IEnumerator IEnumerable.GetEnumerator() => new Enumerator(_spanDictionary);

            void ICollection.CopyTo(Array array, int index)
            {
                ArgumentNullException.ThrowIfNull(array);

                if (array.Rank != 1)
                {
                    throw new ArgumentException("Only single dimensional arrays are supported for the requested action.");
                }

                if (array.GetLowerBound(0) != 0)
                {
                    throw new ArgumentException("The lower bound of target array must be zero.");
                }

                if ((uint)index > (uint)array.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(index), "Non-negative number required.");
                }

                if (array.Length - index < _spanDictionary.Count)
                {
                    // throw new ArgumentException("Destination array is not long enough to copy all the items in the collection. Check array index and length.", nameof(array));
                }

                if (array is TValue[] values)
                {
                    CopyTo(values, index);
                }
                else
                {
                    object[]? objects = array as object[];
                    if (objects == null)
                    {
                        throw new ArgumentException("Target array type is not compatible with the type of items in the collection.");
                    }

                    int count = _spanDictionary._count;
                    Entry[]? entries = _spanDictionary._entries;
                    try
                    {
                        for (int i = 0; i < count; i++)
                        {
                            if (entries![i].next >= -1) objects[index++] = entries[i].value!;
                        }
                    }
                    catch (ArrayTypeMismatchException)
                    {
                        throw new ArgumentException("Target array type is not compatible with the type of items in the collection.");
                    }
                }
            }

            bool ICollection.IsSynchronized => false;

            object ICollection.SyncRoot => ((ICollection)_spanDictionary).SyncRoot;

            public struct Enumerator : IEnumerator<TValue>, IEnumerator
            {
                private readonly SpanDictionary<TKey, TValue> _spanDictionary;
                private int _index;
                private readonly int _version;
                private TValue? _currentValue;

                internal Enumerator(SpanDictionary<TKey, TValue> dictionary)
                {
                    _spanDictionary = dictionary;
                    _version = dictionary._version;
                    _index = 0;
                    _currentValue = default;
                }

                public void Dispose() { }

                public bool MoveNext()
                {
                    if (_version != _spanDictionary._version)
                    {
                        throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");
                    }

                    while ((uint)_index < (uint)_spanDictionary._count)
                    {
                        ref Entry entry = ref _spanDictionary._entries![_index++];

                        if (entry.next >= -1)
                        {
                            _currentValue = entry.value;
                            return true;
                        }
                    }
                    _index = _spanDictionary._count + 1;
                    _currentValue = default;
                    return false;
                }

                public TValue Current => _currentValue!;

                object? IEnumerator.Current
                {
                    get
                    {
                        if (_index == 0 || (_index == _spanDictionary._count + 1))
                        {
                            throw new InvalidOperationException("Enumeration has either not started or has already finished.");
                        }

                        return _currentValue;
                    }
                }

                void IEnumerator.Reset()
                {
                    if (_version != _spanDictionary._version)
                    {
                        throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");
                    }

                    _index = 0;
                    _currentValue = default;
                }
            }
        }
    }
}
