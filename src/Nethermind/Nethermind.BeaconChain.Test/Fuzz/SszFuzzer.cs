// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.SszRest;
using Nethermind.Serialization.Ssz;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Fuzz;

internal static class SszFuzzer
{
    public static readonly int[] Seeds = [0x5EED01, 0x5EED02, 0x5EED03];

    public static readonly TimeSpan CaseBound = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan TargetBound = TimeSpan.FromSeconds(60);
    private const int MaxReportedFailures = 5;

    public static void Run(int seed, IReadOnlyList<byte[]> valid, int iterations, Action<byte[]> target, Func<Exception, bool> isRefusal) =>
        Check($"seed 0x{seed:x}", Mutations(seed, valid, iterations), iterations, target, isRefusal);

    public static void RunPrefixes(IReadOnlyList<byte[]> valid, int maxLength, Action<byte[]> target, Func<Exception, bool> isRefusal)
    {
        List<byte[]> prefixes = [.. valid.SelectMany(bytes => Enumerable.Range(0, Math.Min(bytes.Length, maxLength) + 1).Select(length => bytes[..length]))];
        Check("prefix sweep", prefixes, prefixes.Count, target, isRefusal);
    }

    private static IEnumerable<byte[]> Mutations(int seed, IReadOnlyList<byte[]> valid, int iterations)
    {
        Random random = new(seed);
        for (int i = 0; i < iterations; i++)
        {
            yield return Mutate(random, valid);
        }
    }

    private static void Check(string label, IEnumerable<byte[]> inputs, int count, Action<byte[]> target, Func<Exception, bool> isRefusal)
    {
        List<string> failures = [];
        Task run = Task.Run(() =>
        {
            int i = 0;
            foreach (byte[] input in inputs)
            {
                if (failures.Count >= MaxReportedFailures)
                {
                    break;
                }

                long started = Stopwatch.GetTimestamp();
                try
                {
                    target(input);
                }
                catch (Exception e) when (!isRefusal(e))
                {
                    failures.Add($"input {i}: {e.GetType().Name}: {e.Message}\n  input ({input.Length} bytes): {Preview(input)}\n{e.StackTrace}");
                }
                catch (Exception)
                {
                    // A documented refusal.
                }

                TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
                if (elapsed > CaseBound)
                {
                    failures.Add($"input {i}: took {elapsed.TotalMilliseconds:F0} ms, over the {CaseBound.TotalMilliseconds} ms bound\n  input ({input.Length} bytes): {Preview(input)}");
                }

                i++;
            }
        });

        Assert.That(run.Wait(TargetBound), Is.True, $"{label}: the target did not finish {count} inputs within {TargetBound}");
        Assert.That(failures, Is.Empty, label);
    }

    public static void RoundTrip<T>(byte[] ssz) where T : class, ISszCodec<T>
    {
        T.Decode(ssz, out T value);
        byte[] encoded = T.Encode(value);
        T.Merkleize(value, out UInt256 _);
        if (!encoded.AsSpan().SequenceEqual(ssz))
        {
            throw new AssertionException($"{typeof(T).Name} accepted a non-canonical encoding: re-encoded to {encoded.Length} bytes: {Preview(encoded)}");
        }
    }

    public static byte[][] ValidEncodings<T>(int seed, int count, Action<T>? adjust = null) where T : class, ISszCodec<T>
    {
        SszValueGenerator generator = new(new Random(seed));
        byte[][] encodings = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            T value = generator.Create<T>();
            adjust?.Invoke(value);
            encodings[i] = T.Encode(value);
        }

        return encodings;
    }

    public static byte[] Mutate(Random random, IReadOnlyList<byte[]> valid)
    {
        byte[] bytes = valid[random.Next(valid.Count)];
        if (random.Next(10) == 0)
        {
            return RandomBytes(random, random.Next(0, bytes.Length * 2 + 16));
        }

        int rounds = 1 + random.Next(3);
        for (int round = 0; round < rounds; round++)
        {
            bytes = random.Next(8) switch
            {
                0 => FlipBits(random, bytes),
                1 => SetBytes(random, bytes),
                // Half the truncations keep only a short prefix, where fixed-part and slot reads run out of bytes.
                2 => bytes[..random.Next(0, (random.Next(2) == 0 ? Math.Min(bytes.Length, 128) : bytes.Length) + 1)],
                3 => [.. bytes, .. RandomBytes(random, 1 + random.Next(64))],
                4 => Delete(random, bytes),
                5 => Duplicate(random, bytes),
                6 => Splice(random, bytes, valid[random.Next(valid.Count)]),
                _ => WriteBoundaryWord(random, bytes),
            };
        }

        return bytes;
    }

    public static string Preview(byte[] bytes) =>
        bytes.Length <= 256 ? Convert.ToHexStringLower(bytes) : $"{Convert.ToHexStringLower(bytes.AsSpan(0, 256))}...";

    private static byte[] RandomBytes(Random random, int length)
    {
        byte[] bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    private static byte[] FlipBits(Random random, byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return bytes;
        }

        byte[] copy = [.. bytes];
        for (int i = 1 + random.Next(8); i > 0; i--)
        {
            copy[random.Next(copy.Length)] ^= (byte)(1 << random.Next(8));
        }

        return copy;
    }

    private static byte[] SetBytes(Random random, byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return bytes;
        }

        byte[] copy = [.. bytes];
        for (int i = 1 + random.Next(4); i > 0; i--)
        {
            copy[random.Next(copy.Length)] = random.Next(3) switch { 0 => 0x00, 1 => 0xff, _ => (byte)random.Next(256) };
        }

        return copy;
    }

    private static byte[] Delete(Random random, byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return bytes;
        }

        int start = random.Next(bytes.Length);
        int length = 1 + random.Next(Math.Min(64, bytes.Length - start));
        return [.. bytes.AsSpan(0, start), .. bytes.AsSpan(start + length)];
    }

    private static byte[] Duplicate(Random random, byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return bytes;
        }

        int start = random.Next(bytes.Length);
        int length = 1 + random.Next(Math.Min(64, bytes.Length - start));
        return [.. bytes.AsSpan(0, start + length), .. bytes.AsSpan(start)];
    }

    private static byte[] Splice(Random random, byte[] first, byte[] second)
    {
        int cut = random.Next(first.Length + 1);
        return [.. first.AsSpan(0, cut), .. second.AsSpan(Math.Min(cut, second.Length))];
    }

    // SSZ offsets are 4-byte little-endian words, so boundary values at word positions reach the offset checks.
    private static byte[] WriteBoundaryWord(Random random, byte[] bytes)
    {
        if (bytes.Length < sizeof(uint))
        {
            return bytes;
        }

        byte[] copy = [.. bytes];
        int position = random.Next(copy.Length - sizeof(uint) + 1);
        if (random.Next(2) == 0)
        {
            position &= ~3;
        }

        uint length = (uint)copy.Length;
        uint value = random.Next(12) switch
        {
            0 => 0,
            1 => 1,
            2 => 4,
            3 => length - 1,
            4 => length,
            5 => length + 1,
            6 => length - 4,
            7 => 0x7fffffff,
            8 => 0x80000000,
            9 => 0xffffffff,
            10 => (uint)position,
            _ => (uint)random.Next(),
        };
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(position), value);
        return copy;
    }
}

internal sealed class SszValueGenerator(Random random)
{
    private const int MaxListItems = 3;
    private const int MaxListBytes = 48;
    private const int MaxListWords = 12;
    private const int MaxBits = 70;

    public T Create<T>() where T : class => (T)CreateContainer(typeof(T));

    private object CreateContainer(Type type)
    {
        object value = Activator.CreateInstance(type)!;
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanWrite && property.GetCustomAttribute<SszIgnoreAttribute>() is null)
            {
                property.SetValue(value, CreateValue(property.PropertyType, property));
            }
        }

        return value;
    }

    private object CreateValue(Type type, PropertyInfo property)
    {
        int? vectorLength = property.GetCustomAttribute<SszVectorAttribute>()?.Length;
        ulong? limit = property.GetCustomAttribute<SszListAttribute>()?.Limit;
        if (type == typeof(BitArray))
        {
            BitArray bits = new(vectorLength ?? Count(limit, MaxBits));
            for (int i = 0; i < bits.Length; i++)
            {
                bits[i] = random.Next(2) == 1;
            }

            return bits;
        }

        if (type.IsArray)
        {
            Type elementType = type.GetElementType()!;
            int max = elementType == typeof(byte) ? MaxListBytes : elementType == typeof(ulong) ? MaxListWords : MaxListItems;
            Array items = Array.CreateInstance(elementType, vectorLength ?? Count(limit, max));
            for (int i = 0; i < items.Length; i++)
            {
                items.SetValue(CreateElement(elementType), i);
            }

            return items;
        }

        return CreateElement(type);
    }

    private int Count(ulong? limit, int max) => random.Next(0, (int)Math.Min(limit ?? (ulong)max, (ulong)max) + 1);

    private object CreateElement(Type type)
    {
        if (type == typeof(ulong)) return NextUlong();
        if (type == typeof(byte)) return (byte)random.Next(256);
        if (type == typeof(bool)) return random.Next(2) == 1;
        if (type == typeof(UInt256)) return new UInt256(NextUlong(), NextUlong(), NextUlong(), NextUlong());
        if (type == typeof(Hash256)) return new Hash256(Bytes(Hash256.Size));
        if (type == typeof(Address)) return new Address(Bytes(Address.Size));
        if (type == typeof(Bloom)) return new Bloom(Bytes(Bloom.ByteLength));
        if (type == typeof(BlsSignature)) return new BlsSignature(Bytes(BlsSignature.Length));
        if (type == typeof(BlsPublicKey)) return new BlsPublicKey(Bytes(BlsPublicKey.Length));
        if (type == typeof(SszKzgCommitment)) return SszKzgCommitment.FromSpan(Bytes(SszKzgCommitment.KzgCommitmentLength));
        if (type == typeof(SszBlobCell)) return SszBlobCell.FromSpan(Bytes(SszBlobCell.BlobCellLength));
        if (type.GetCustomAttribute<SszContainerAttribute>() is not null) return CreateContainer(type);
        throw new NotSupportedException($"No random value generator for {type.Name}");
    }

    private ulong NextUlong() => random.Next(4) == 0 ? (ulong)random.Next(64) : (ulong)random.NextInt64() ^ ((ulong)random.Next(2) << 63);

    private byte[] Bytes(int length)
    {
        byte[] bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }
}
