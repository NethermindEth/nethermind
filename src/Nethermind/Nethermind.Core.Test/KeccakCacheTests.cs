// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.IO;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class KeccakCacheTests
    {
        [Test]
        public void Capacity_matches_the_startup_switch()
        {
            bool small = AppContext.TryGetSwitch("Nethermind.Crypto.KeccakCache.Small", out bool enabled) && enabled;
            Assert.That(KeccakCache.Count, Is.EqualTo((nuint)(small ? 32 * 1024 : 512 * 1024)));
        }

        [Test]
        public async Task Small_cache_preserves_hashes_in_an_isolated_process()
        {
            using TempPath temporary = TempPath.GetTempDirectory();
            Directory.CreateDirectory(temporary.Path);
            string assembly = typeof(KeccakCacheTests).Assembly.Location;
            JsonNode config = JsonNode.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(assembly, "runtimeconfig.json")))!;
            config["runtimeOptions"]!["configProperties"]!["Nethermind.Crypto.KeccakCache.Small"] = true;
            string runtimeConfig = Path.Combine(temporary.Path, "small.runtimeconfig.json");
            await File.WriteAllTextAsync(runtimeConfig, config.ToJsonString());
            ProcessStartInfo start = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in new[]
            {
                "exec", "--runtimeconfig", runtimeConfig, assembly,
                "--filter", $"FullyQualifiedName~{nameof(KeccakCacheTests)}&FullyQualifiedName!~{nameof(Small_cache_preserves_hashes_in_an_isolated_process)}",
                "--results-directory", temporary.Path,
            }) start.ArgumentList.Add(argument);
            using Process child = new() { StartInfo = start };
            Assert.That(child.Start(), Is.True);
            Task<string> output = child.StandardOutput.ReadToEndAsync();
            Task<string> error = child.StandardError.ReadToEndAsync();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
            try { await child.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
                Assert.Fail($"Small-cache tests timed out.\n{await output}\n{await error}");
            }
            Assert.That(child.ExitCode, Is.Zero, $"{await output}\n{await error}");
        }

        [Test]
        [NonParallelizable]
        public void Hash_bits_reserved_for_synchronization_do_not_prevent_a_hit([Values(20, 32, 64, 92)] int length)
        {
            byte[] input = new byte[length];
            Random random = new(1319);
            do { random.NextBytes(input); } while ((((ReadOnlySpan<byte>)input).FastHash() & 0x8000) == 0);
            ValueHash256 expected = ValueKeccak.Compute(input);
            KeccakCache.Compute(input);
            Assert.That(KeccakCache.TryGet(input, out ValueHash256 actual), Is.True);
            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void Multiple()
        {
            const int spins = 10;

            Random random = new(13);
            byte[] bytes = new byte[31]; // misaligned length
            random.NextBytes(bytes);

            ValueHash256 expected = ValueKeccak.Compute(bytes);

            for (int i = 0; i < spins; i++)
            {
                ValueHash256 actual = KeccakCache.Compute(bytes);
                Assert.That(actual.Equals(expected), Is.True);
            }
        }

        [Test]
        public void Empty()
        {
            ReadOnlySpan<byte> span = [];
            Assert.That(KeccakCache.Compute(span), Is.EqualTo(ValueKeccak.Compute(span)));
        }

        [Test]
        public void Very_long()
        {
            ReadOnlySpan<byte> span = new byte[192];
            Assert.That(KeccakCache.Compute(span), Is.EqualTo(ValueKeccak.Compute(span)));
        }

        [Test]
        public void Unsupported_lengths_are_not_cached([Values(0, 93, 128, 192)] int length)
        {
            byte[] input = new byte[length];
            new Random(513).NextBytes(input);
            ValueHash256 expected = ValueKeccak.Compute(input);
            Assert.That(KeccakCache.TryGet(input, out _), Is.False);
            KeccakCache.Store(input, in expected);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(KeccakCache.TryGet(input, out _), Is.False);
                Assert.That(KeccakCache.Compute(input), Is.EqualTo(expected));
            }
        }

        [Test]
        [NonParallelizable]
        public void Output_aliasing_the_input_caches_the_input([Values(20, 32, 64)] int length)
        {
            byte[] input = new byte[length];
            new Random(7919 + length).NextBytes(input);
            ValueHash256 expected = ValueKeccak.Compute(input);
            byte[] buffer = new byte[Math.Max(length, ValueHash256.MemorySize)];
            input.CopyTo(buffer, 0);
            ref ValueHash256 output = ref Unsafe.As<byte, ValueHash256>(ref buffer[0]);

            KeccakCache.ComputeTo(buffer.AsSpan(0, length), out output);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(output, Is.EqualTo(expected));
                Assert.That(KeccakCache.TryGet(input, out ValueHash256 cached), Is.True);
                Assert.That(cached, Is.EqualTo(expected));
            }
        }

        private string[] GetBucketCollisions()
        {
            Random random = new(13);
            Span<byte> span = stackalloc byte[32];
            string[] collisions = new string[4];

            random.NextBytes(span);
            uint bucket = KeccakCache.GetBucket(span);

            Console.WriteLine(span.ToHexString());

            collisions[0] = span.ToHexString();
            int found = 1;

            ulong iterations = 0;
            while (found < 4)
            {
                random.NextBytes(span);
                if (KeccakCache.GetBucket(span) == bucket)
                {
                    collisions[found] = span.ToHexString();
                    Console.WriteLine(span.ToHexString());
                    found++;
                }
                iterations++;
            }

            Console.WriteLine($"{iterations} iterations to find");
            return collisions;
        }

        [Test]
        public void Collision()
        {
            string[] colliding = GetBucketCollisions();

            int collisions = colliding.Length;
            byte[][] array = colliding.Select(c => Bytes.FromHexString(c)).ToArray();
            ValueHash256[] values = array.Select(a => ValueKeccak.Compute(a)).ToArray();

            uint bucket = KeccakCache.GetBucket(array[0]);

            for (int i = 1; i < collisions; i++)
            {
                byte[] input = array[i];
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(bucket, Is.EqualTo(KeccakCache.GetBucket(input)));
                    Assert.That(KeccakCache.Compute(input), Is.EqualTo(values[i]));
                }
            }

            Parallel.ForEach(array, (a, state, index) =>
            {
                ValueHash256 v = values[index];

                for (int i = 0; i < 100_000; i++)
                {
                    Assert.That(KeccakCache.Compute(a), Is.EqualTo(v));
                }
            });
        }

        [Test]
        public void Spin_through_all()
        {
            Span<byte> span = stackalloc byte[4];
            for (int i = 0; i < (int)KeccakCache.Count; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(span, i);
                Assert.That(KeccakCache.Compute(span), Is.EqualTo(ValueKeccak.Compute(span)));
            }
        }

        [Test]
        [NonParallelizable]
        public void Compute_WithUnalignedInput_MatchesUncachedHash(
            [Values(20, 32, 63, 64, 65, 92)] int length,
            [Values(0, 1, 7, 15)] int offset,
            [Values] bool storeComputedHash)
        {
            Random random = new(42);
            for (int i = 0; i < 1000; i++)
            {
                byte[] buffer = new byte[length + offset];
                random.NextBytes(buffer);
                ReadOnlySpan<byte> bytes = buffer.AsSpan(offset, length);

                ValueHash256 expected = ValueKeccak.Compute(bytes);
                if (storeComputedHash) KeccakCache.Store(bytes, in expected);
                ValueHash256 actual = KeccakCache.Compute(bytes);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(actual, Is.EqualTo(expected));

                    // Second call should hit cache
                    Assert.That(KeccakCache.Compute(bytes), Is.EqualTo(expected));
                    Assert.That(KeccakCache.TryGet(bytes, out ValueHash256 cached), Is.True);
                    Assert.That(cached, Is.EqualTo(expected));
                }
            }
        }

        [Test]
        public void Concurrent_read_write_stress([Values(20, 32, 64)] int length, [Values] bool storeComputedHash)
        {
            // Stress test the seqlock pattern with concurrent readers and writers
            const int iterations = 100_000;
            byte[] bytes = new byte[length];
            new Random(123).NextBytes(bytes);

            // Prime the cache
            KeccakCache.Compute(bytes);

            // Create different keys that hash to the same bucket (will cause cache eviction)
            byte[][] collisions = new byte[4][];
            collisions[0] = bytes;
            Random random = new(456);
            uint bucket = KeccakCache.GetBucket(bytes);
            int found = 1;
            while (found < 4)
            {
                byte[] candidate = new byte[length];
                random.NextBytes(candidate);
                if (KeccakCache.GetBucket(candidate) == bucket)
                {
                    collisions[found++] = candidate;
                }
            }

            ValueHash256[] expectedValues = collisions.Select(c => ValueKeccak.Compute(c)).ToArray();

            // Parallel readers and writers hammering the same bucket
            Parallel.For(0, iterations, i =>
            {
                int idx = i % 4;
                byte[] input = collisions[idx];
                if (storeComputedHash) KeccakCache.Store(input, in expectedValues[idx]);
                ValueHash256 result = KeccakCache.Compute(input);
                Assert.That(result, Is.EqualTo(expectedValues[idx]), $"iteration {i}, index {idx}");
                if (KeccakCache.TryGet(input, out ValueHash256 cached))
                    Assert.That(cached, Is.EqualTo(expectedValues[idx]), $"cached iteration {i}, index {idx}");
            });
        }
    }
}
