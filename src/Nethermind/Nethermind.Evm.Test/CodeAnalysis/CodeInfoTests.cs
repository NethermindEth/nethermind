// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Evm.CodeAnalysis;
using NUnit.Framework;

namespace Nethermind.Evm.Test.CodeAnalysis
{
    [TestFixture]
    public class CodeInfoTests
    {
        [Test]
        [Repeat(10)]
        public async Task Concurrent_analysis_publishes_complete_bitmap(
            [Values(64, 66, 32768)] int length, [Values] bool analysisCompletesFirst)
        {
            const int Workers = 4;
            TimeSpan timeout = TimeSpan.FromSeconds(30);
            byte[] code = GroupedJumpDestinations(length);
            TaskCompletionSource analysisStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource continueAnalysis = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource startReaders = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using GatedCodeMemory memory = new(code, () =>
            {
                // Execute claims analysis before requesting the code span.
                analysisStarted.TrySetResult();
                continueAnalysis.Task.GetAwaiter().GetResult();
            });
            CodeInfo codeInfo = new(memory.Memory);
            Task[] workers = new Task[Workers];
            Array.Fill(workers, Task.CompletedTask);
            ExceptionDispatchInfo? failure = null;
            try
            {
                workers[0] = Task.Factory.StartNew(((IThreadPoolWorkItem)codeInfo).Execute,
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                await analysisStarted.Task.WaitAsync(timeout);
                for (int worker = 1; worker < workers.Length; worker++)
                {
                    workers[worker] = Task.Run(async () =>
                    {
                        await startReaders.Task;
                        AssertGroupedJumpDestinations(codeInfo, length);
                    });
                }
                continueAnalysis.TrySetResult();
                // Cover completed fast-path reads separately from scheduling-dependent publication races.
                if (analysisCompletesFirst) await workers[0].WaitAsync(timeout);
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                continueAnalysis.TrySetResult();
                startReaders.TrySetResult();
            }

            try
            {
                await Task.WhenAll(workers).WaitAsync(timeout);
            }
            catch (Exception exception)
            {
                failure ??= ExceptionDispatchInfo.Capture(exception);
            }
            failure?.Throw();
        }

        [Test]
        [Repeat(10)]
        public async Task Concurrent_first_use_publishes_complete_bitmap([Values(64, 66, 32768)] int length)
        {
            // Without a background analysis every reader may analyze the code itself; each must see a complete bitmap.
            const int Readers = 4;
            CodeInfo codeInfo = new(GroupedJumpDestinations(length));
            TaskCompletionSource startReaders = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task[] readers = new Task[Readers];
            for (int reader = 0; reader < readers.Length; reader++)
            {
                readers[reader] = Task.Run(async () =>
                {
                    await startReaders.Task;
                    AssertGroupedJumpDestinations(codeInfo, length);
                });
            }

            startReaders.TrySetResult();
            await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(30));
        }

        private const int GroupSize = 4;
        private const int JumpDestOffset = 2;

        /// <summary>Repeats PUSH1 JUMPDEST JUMPDEST STOP, so the third byte of each whole group is the only jump destination.</summary>
        private static byte[] GroupedJumpDestinations(int length)
        {
            byte[] code = new byte[length];
            for (int i = 0; i <= length - GroupSize; i += GroupSize)
            {
                code[i] = (byte)Instruction.PUSH1;
                code[i + 1] = (byte)Instruction.JUMPDEST;
                code[i + JumpDestOffset] = (byte)Instruction.JUMPDEST;
            }

            return code;
        }

        private static void AssertGroupedJumpDestinations(CodeInfo codeInfo, int length)
        {
            int mismatch = -1;
            for (int offset = 0; offset < length && mismatch < 0; offset++)
            {
                bool expected = offset < length - length % GroupSize && offset % GroupSize == JumpDestOffset;
                if (codeInfo.ValidateJump(offset) != expected) mismatch = offset;
            }
            Assert.That(mismatch, Is.EqualTo(-1), "first offset with an unexpected jump-destination bit");
        }

        [Test]
        public async Task Analysis_failure_is_reported_to_later_readers([Values] bool background)
        {
            InvalidOperationException expected = new("analysis failed");
            using GatedCodeMemory memory = new([(byte)Instruction.JUMPDEST], () => throw expected);
            CodeInfo codeInfo = new(memory.Memory);
            if (background) ((IThreadPoolWorkItem)codeInfo).Execute();

            for (int reader = 0; reader < 2; reader++)
            {
                Exception? actual = await Task.Run(() =>
                {
                    try
                    {
                        codeInfo.ValidateJump(0);
                        return null;
                    }
                    catch (Exception exception)
                    {
                        return exception;
                    }
                }).WaitAsync(TimeSpan.FromSeconds(30));
                Assert.That(actual, Is.SameAs(expected));
            }
        }

        [TestCase(-1, false)]
        [TestCase(0, true)]
        [TestCase(1, false)]
        public void Validates_when_only_jump_dest_present(int destination, bool isValid)
        {
            byte[] code =
            {
                (byte)Instruction.JUMPDEST
            };

            CodeInfo codeInfo = new(code);

            Assert.That(codeInfo.ValidateJump(destination), Is.EqualTo(isValid));
        }

        [Test]
        public void Validates_when_push_with_data_like_jump_dest()
        {
            byte[] code =
            {
                (byte)Instruction.PUSH1,
                (byte)Instruction.JUMPDEST
            };

            CodeInfo codeInfo = new(code);

            Assert.That(codeInfo.ValidateJump(1), Is.False);
        }

        [Test]
        public void Validate_CodeBitmap_With_Push10()
        {
            byte[] code =
            {
                (byte)Instruction.PUSH10,
                1,2,3,4,5,6,7,8,9,10,
                (byte)Instruction.JUMPDEST
            };

            CodeInfo codeInfo = new(code);

            Assert.That(codeInfo.ValidateJump(11), Is.True);
        }

        [Test]
        public void Validate_CodeBitmap_With_Push30()
        {
            byte[] code =
            {
                (byte)Instruction.PUSH30,
                1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,
                (byte)Instruction.JUMPDEST
            };

            CodeInfo codeInfo = new(code);

            Assert.That(codeInfo.ValidateJump(31), Is.True);
        }

        [Test]
        public void Small_Jumpdest()
        {
            byte[] code =
            {
                0x5b,0x5b,0x5b,0x5b,0x5b,0x5b,0x5b,0x5b,0x5b,0x5b,0x5b,0x5b,0x5b,0x5b,0x5b,0x5b,0x5b,0x5b,0x5b,0x5b
            };

            CodeInfo codeInfo = new(code);

            Assert.That(codeInfo.ValidateJump(10), Is.True);
        }

        [Test]
        public void Small_Push1()
        {
            byte[] code =
            {
                0x60,0x60,0x60,0x60,0x60,0x60,0x60,0x60,0x60,0x60,0x60,0x60,0x60,0x60,0x60,0x60,0x60,0x60,0x60,0x60,0x60,0x60,
            };

            CodeInfo codeInfo = new(code);

            Assert.That(codeInfo.ValidateJump(10), Is.False);
        }

        [Test]
        public void Jumpdest_Over10k()
        {
            byte[] code = Enumerable.Repeat((byte)0x5b, 10_001).ToArray();

            CodeInfo codeInfo = new(code);

            Assert.That(codeInfo.ValidateJump(10), Is.True);
        }

        [Test]
        public void Push1_Over10k()
        {
            byte[] code = Enumerable.Repeat((byte)0x60, 10_001).ToArray();

            CodeInfo codeInfo = new(code);

            Assert.That(codeInfo.ValidateJump(10), Is.False);
        }

        [Test]
        public void Push1Jumpdest_Over10k()
        {
            byte[] code = new byte[10_001];
            for (int i = 0; i < code.Length; i++)
            {
                code[i] = i % 2 == 0 ? (byte)0x60 : (byte)0x5b;
            }

            CodeInfo codeInfo = new(code);

            Assert.That(codeInfo.ValidateJump(10), Is.False);
            Assert.That(codeInfo.ValidateJump(11), Is.False); // 0x5b but not JUMPDEST but data
        }

        [Test]
        public void PushNJumpdest_Over10k([Range(1, 32)] int n)
        {
            byte[] code = new byte[10_001];

            // One vector (aligned), half vector to unalign
            int i;
            for (i = 0; i < Vector256<byte>.Count * 2 + Vector128<byte>.Count; i++)
            {
                code[i] = (byte)0x5b;
            }
            for (; i < Vector256<byte>.Count * 3; i++)
            {
                //
            }
            bool triggerPushes = false;
            for (; i < code.Length; i++)
            {
                if (i % (n + 1) == 0)
                {
                    triggerPushes = true;
                }
                if (triggerPushes)
                {
                    code[i] = i % (n + 1) == 0 ? (byte)(0x60 + n - 1) : (byte)0x5b;
                }
            }

            CodeInfo codeInfo = new(code);

            for (i = 0; i < Vector256<byte>.Count * 2 + Vector128<byte>.Count; i++)
            {
                Assert.That(codeInfo.ValidateJump(i), Is.True);
            }
            for (; i < Vector256<byte>.Count * 3; i++)
            {
                Assert.That(codeInfo.ValidateJump(i), Is.False);
            }
            for (; i < code.Length; i++)
            {
                Assert.That(codeInfo.ValidateJump(i), Is.False); // Are 0x5b but not JUMPDEST but data
            }
        }

        [TestCaseSource(nameof(Codes))]
        public void JumpDestinationAnalyzer_are_equivalent(byte[] codeInput)
        {
            for (int i = 1; i <= codeInput.Length; i++)
            {
                AssertKernelsMatchReference(codeInput.AsSpan(0, i));
            }

            AssertAnalysisMatchesReference(codeInput);
        }

        [Test]
        public void Every_opcode_matches_the_reference_at_block_edges([Range(0, 255)] int op)
        {
            byte[] code = new byte[200];
            foreach (int position in BlockEdgeOffsets)
            {
                code.AsSpan().Fill((byte)Instruction.JUMPDEST);
                code[position] = (byte)op;
                AssertKernelsMatchReference(code);
            }
        }

        /// <param name="background">0: JUMPDEST everywhere; 1: STOP everywhere; 2: PUSH1 JUMPDEST pairs before the push and JUMPDEST after it.</param>
        [Test]
        public void Every_push_matches_the_reference_at_every_offset([Range((int)Instruction.PUSH1, (int)Instruction.PUSH32)] int push, [Range(0, 2)] int background)
        {
            byte[] code = new byte[256];
            for (int position = 0; position < 200; position++)
            {
                code.AsSpan().Fill(background == 1 ? (byte)Instruction.STOP : (byte)Instruction.JUMPDEST);
                if (background == 2)
                {
                    for (int i = 0; i < position; i += 2) code[i] = (byte)Instruction.PUSH1;
                }

                code[position] = (byte)push;
                AssertKernelsMatchReference(code);
            }
        }

        [Test]
        public void Random_bytecode_matches_the_reference([Range(0, 59)] int seed)
        {
            byte[][] alphabets =
            [
                [0x00, 0x5b], [0x00, 0x5b, 0x60], [0x5b, 0x60], [0x5b, 0x60, 0x61, 0x7f], [0x60, 0x7f, 0x5b, 0x70],
                [0x5b, 0x60, 0x61], [0x60, 0x61], [0x7f], [0x00],
            ];
            uint state = ((uint)seed * 2654435761u) | 1u;
            for (int sample = 0; sample < 40; sample++)
            {
                int length = 1 + (int)(NextRandom(ref state) % (sample % 10 == 0 ? 5000u : 700u));
                byte[] code = new byte[length];
                byte[] alphabet = alphabets[seed % alphabets.Length];
                for (int i = 0; i < length; i++)
                {
                    // Seeds 0..19 draw any byte; 20..39 keep one alphabet; 40..59 switch alphabet every 64 bytes.
                    if (seed >= 40 && (i & 63) == 0) alphabet = alphabets[NextRandom(ref state) % (uint)alphabets.Length];
                    code[i] = seed < 20 ? (byte)NextRandom(ref state) : alphabet[NextRandom(ref state) % (uint)alphabet.Length];
                }

                AssertKernelsMatchReference(code);
            }
        }

        [TestCaseSource(nameof(EdgeCases))]
        public void Edge_cases_match_the_reference(byte[] code)
        {
            AssertKernelsMatchReference(code);
            AssertAnalysisMatchesReference(code);
        }

        private static IEnumerable<TestCaseData> EdgeCases()
        {
            // Two blocks analyzed together must each be lifted from their own entry. A PUSH2 keeps a pair off the
            // PUSH1 carry arithmetic, which would otherwise decode it without lifting.
            yield return Case("JUMPDEST at 0 and PUSH2 at 126", Filled(128, 0x00, (0, 0x5b), (126, 0x61)));
            yield return Case("JUMPDEST at 0, PUSH2 at 126, JUMPDEST at 129", Filled(192, 0x00, (0, 0x5b), (126, 0x61), (129, 0x5b)));
            // Pairs without PUSH2..PUSH32 take the PUSH1 carry arithmetic, which must honor PUSH data carried in from
            // a general pair, carry a PUSH1 at the last byte of a block or pair, and hand that carry back.
            yield return Case("JUMPDEST at 0 and PUSH1 at 127 before two JUMPDESTs", Filled(130, 0x00, (0, 0x5b), (127, 0x60), (128, 0x5b), (129, 0x5b)));
            yield return Case("PUSH2 at 126 carries into a PUSH1 at 128", Filled(256, 0x5b, (126, 0x61), (128, 0x60)));
            yield return Case("PUSH2 at 126 carries into the first of two pairs without PUSH", Filled(384, 0x5b, (126, 0x61)));
            yield return Case("PUSH1 at 63 carries into a PUSH1 at 64", Filled(128, 0x5b, (63, 0x60), (64, 0x60)));
            yield return Case("PUSH1 at 127 carries into a pair with a PUSH2", Filled(256, 0x5b, (127, 0x60), (200, 0x61)));
            yield return Case("Odd 0x60 run filling block B carries into the next pair", [.. Filled(128, 0x60, (0, 0x5b)), .. Filled(128, 0x5b)]);
            yield return Case("Even 0x60 run filling block B carries nothing", [.. Filled(128, 0x60, (0, 0x5b), (1, 0x5b)), .. Filled(128, 0x5b)]);
            // A block without a JUMPDEST still carries PUSH data into the next block.
            yield return Case("PUSH4 at 62 covers JUMPDESTs at 64 to 66", Filled(128, 0x00, (62, 0x63), (64, 0x5b), (65, 0x5b), (66, 0x5b), (67, 0x5b)));
            // PUSH32 data covers two whole 16-byte blocks.
            yield return Case("PUSH32 at 15 among JUMPDESTs", Filled(80, 0x5b, (15, 0x7f)));
            // PUSH bytes inside data before the entry must not start instructions.
            yield return Case("PUSH32 data holding PUSH32 bytes", Filled(160, 0x5b, (40, 0x7f), (64, 0x7f), (66, 0x7f), (70, 0x7f), (72, 0x7f)));
            yield return Case("PUSH1 run over offsets 60 to 70", Filled(128, 0x5b, (60, 0x60), (61, 0x60), (62, 0x60), (63, 0x60), (64, 0x60), (65, 0x60), (66, 0x60), (67, 0x60), (68, 0x60), (69, 0x60), (70, 0x60)));
            // EIP-8024: DUPN, SWAPN and EXCHANGE immediates are not skipped by jump-destination analysis.
            yield return Case("DUPN JUMPDEST", [0xe6, 0x5b]);
            yield return Case("DUPN PUSH1 JUMPDEST", [0xe6, 0x60, 0x5b]);
            yield return Case("DUPN, SWAPN and EXCHANGE before JUMPDESTs in 70 bytes", Filled(70, 0x00, (0, 0x5b), (40, 0xe6), (41, 0x5b), (60, 0xe7), (61, 0x5b), (62, 0xe8), (63, 0x5b)));
            yield return Case("PUSH32 as the last byte", Filled(129, 0x5b, (128, 0x7f)));
            yield return Case("64 KiB STOP body", Initcode64KiB(static (_, _) => 0x00));
            yield return Case("64 KiB PUSH1 JUMPDEST body", Initcode64KiB(static (_, i) => (i & 1) == 0 ? (byte)0x60 : (byte)0x5b));
            yield return Case("64 KiB random STOP JUMPDEST", Initcode64KiB(static (random, _) => Pick(random, 0x00, 0x5b)));
            yield return Case("64 KiB random STOP JUMPDEST PUSH1", Initcode64KiB(static (random, _) => Pick(random, 0x00, 0x5b, 0x60)));
            yield return Case("64 KiB random JUMPDEST PUSH1", Initcode64KiB(static (random, _) => Pick(random, 0x5b, 0x60)));
            yield return Case("64 KiB random JUMPDEST PUSH1 PUSH2 PUSH3", Initcode64KiB(static (random, _) => Pick(random, 0x5b, 0x60, 0x61, 0x62)));
        }

        private static readonly int[] BlockEdgeOffsets = [0, 1, 14, 15, 16, 17, 31, 32, 33, 62, 63, 64, 65, 126, 127, 128, 129];

        private static TestCaseData Case(string name, byte[] code) => new TestCaseData(code).SetName($"{{m}}({name})");

        private static byte[] Filled(int length, byte fill, params (int Position, byte Op)[] ops)
        {
            byte[] code = new byte[length];
            code.AsSpan().Fill(fill);
            foreach ((int position, byte op) in ops) code[position] = op;
            return code;
        }

        private static byte Pick(Random random, params byte[] ops) => ops[random.Next(ops.Length)];

        /// <summary>
        /// 64 KiB initcode shaped like the execution-specs JUMPDEST benchmarks: PUSH2 0xffff JUMP, 32 bytes that
        /// change per CREATE, the body, and 32 trailing JUMPDESTs.
        /// </summary>
        private static byte[] Initcode64KiB(Func<Random, int, byte> body)
        {
            Random random = new(0);
            byte[] code = new byte[65536];
            for (int i = 0; i < code.Length; i++) code[i] = body(random, i);
            code[0] = (byte)Instruction.PUSH2;
            code[1] = 0xff;
            code[2] = 0xff;
            code[3] = (byte)Instruction.JUMP;
            for (int i = 4; i < 36; i++) code[i] = (byte)random.Next(256);
            code.AsSpan(code.Length - 32).Fill((byte)Instruction.JUMPDEST);
            return code;
        }

        private static uint NextRandom(ref uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }

        /// <summary>Walks the code from offset 0, marking JUMPDEST and skipping PUSH1..PUSH32 data.</summary>
        private static long[] ReferenceBitmap(ReadOnlySpan<byte> code)
        {
            long[] bitmap = JumpDestinationAnalyzer.CreateBitmap(code.Length);
            int position = 0;
            while (position < code.Length)
            {
                byte op = code[position];
                if (op == (byte)Instruction.JUMPDEST) bitmap[position >> 6] |= 1L << position;
                position += op is >= (byte)Instruction.PUSH1 and <= (byte)Instruction.PUSH32 ? op - (byte)Instruction.PUSH1 + 2 : 1;
            }

            return bitmap;
        }

        /// <summary>Checks every kernel the hardware supports against <see cref="ReferenceBitmap"/>.</summary>
        private static void AssertKernelsMatchReference(ReadOnlySpan<byte> code)
        {
            long[] expected = ReferenceBitmap(code);
            AssertBitmap("scalar", JumpDestinationAnalyzer.PopulateJumpDestinationBitmap_Scalar(JumpDestinationAnalyzer.CreateBitmap(code.Length), code), expected, code);
            if (Ssse3.IsSupported)
            {
                AssertBitmap("Ssse3", JumpDestinationAnalyzer.PopulateJumpDestinationBitmap_Ssse3(JumpDestinationAnalyzer.CreateBitmap(code.Length), code), expected, code);
            }

            if (AdvSimd.Arm64.IsSupported)
            {
                AssertBitmap("AdvSimd", JumpDestinationAnalyzer.PopulateJumpDestinationBitmap_AdvSimd(JumpDestinationAnalyzer.CreateBitmap(code.Length), code), expected, code);
            }

            if (Avx2.IsSupported)
            {
                AssertBitmap("Vector256", JumpDestinationAnalyzer.PopulateJumpDestinationBitmap_Vector256(JumpDestinationAnalyzer.CreateBitmap(code.Length), code), expected, code);
            }

            if (Avx512Vbmi.IsSupported && Vector512.IsHardwareAccelerated)
            {
                AssertBitmap("Vector512", JumpDestinationAnalyzer.PopulateJumpDestinationBitmap_Vector512(JumpDestinationAnalyzer.CreateBitmap(code.Length), code), expected, code);
            }
        }

        private static void AssertBitmap(string kernel, long[] actual, long[] expected, ReadOnlySpan<byte> code)
        {
            if (actual.AsSpan().SequenceEqual(expected)) return;

            int offset = 0;
            while (offset < code.Length && JumpDestinationAnalyzer.IsJumpDestination(actual, offset) == JumpDestinationAnalyzer.IsJumpDestination(expected, offset)) offset++;
            int start = Math.Max(0, offset - 64);
            Assert.Fail($"{kernel}: length {code.Length}, first wrong offset {offset}, code from offset {start}: {Convert.ToHexString(code[start..Math.Min(code.Length, offset + 64)])}");
        }

        /// <summary>Checks the production analysis through <see cref="CodeInfo.ValidateJump"/>, including its STOP-at-offset-0 shortcut.</summary>
        private static void AssertAnalysisMatchesReference(byte[] code)
        {
            long[] expected = code.Length == 0 || code[0] == (byte)Instruction.STOP ? JumpDestinationAnalyzer.EmptyBitmap : ReferenceBitmap(code);
            CodeInfo codeInfo = new(code);
            int mismatch = -1;
            for (int offset = 0; offset < code.Length && mismatch < 0; offset++)
            {
                if (codeInfo.ValidateJump(offset) != JumpDestinationAnalyzer.IsJumpDestination(expected, offset)) mismatch = offset;
            }

            Assert.That(mismatch, Is.EqualTo(-1), $"first offset where CodeInfo disagrees with the reference, length {code.Length}");
        }

        private static IEnumerable Codes
        {
            get
            {
                byte[] code = new byte[1024];
                TestCaseData test = new(code);
                test.TestName = "Code_All_0x00";
                yield return test;

                // Runs of plain one-byte instructions bracketing each width a scan steps over in one go: the
                // 8-byte scalar word, the 16-byte Vector128 block, the 32-byte PUSH32 payload and the 64-byte
                // Vector512 chunk. The per-prefix loop also cuts every run off at the end of the code.
                int[] runLengths = [1, 6, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65];
                byte[] markers = [(byte)Instruction.JUMPDEST, (byte)Instruction.PUSH1, (byte)Instruction.PUSH32];
                foreach (int run in runLengths)
                {
                    foreach (byte marker in markers)
                    {
                        code = new byte[1024];
                        for (int i = run; i < code.Length; i += run + 1)
                        {
                            code[i] = marker;
                        }

                        test = new TestCaseData(code);
                        test.TestName = $"Code_Run{run}_{(Instruction)marker}";
                        yield return test;
                    }
                }

                code = new byte[1024];
                code.AsSpan().Fill((byte)0x5b);
                test = new TestCaseData(code);
                test.TestName = "Code_All_JUMPDEST";
                yield return test;

                code = new byte[1024];
                for (int i = 8; i < code.Length - 3; i += 4)
                {
                    code[i] = (byte)Instruction.JUMPDEST;
                    code[i + 1] = (byte)Instruction.PUSH1;
                    code[i + 2] = (byte)Instruction.JUMPDEST;
                    code[i + 3] = (byte)Instruction.JUMPDEST;
                }
                test = new TestCaseData(code);
                test.TestName = "Code_Unaligned_PUSH1_JUMPDEST";
                yield return test;

                for (int start = 0; start <= 1; start++)
                {
                    for (int push = 0x60; push <= 0x7f; push++)
                    {
                        code = new byte[1024];
                        for (int i = 0; i < code.Length; i++)
                        {
                            code[i] = (i + start) % 2 == 0 ? (byte)push : (byte)0x5b;
                        }
                        test = new TestCaseData(code);
                        test.TestName = start == 0 ?
                            $"Code_All_PUSH{push - 0x5f:00}JUMPDEST" :
                            $"Code_All_JUMPDESTPUSH{push - 0x5f:00}";
                        yield return test;
                    }
                }
            }
        }

        // Span callbacks control analysis without adding a production test hook; disposal owns no resources.
        private sealed class GatedCodeMemory(byte[] code, Action beforeRead) : MemoryManager<byte>
        {
            public override Memory<byte> Memory => CreateMemory(code.Length);
            public override Span<byte> GetSpan()
            {
                beforeRead();
                return code;
            }
            public override MemoryHandle Pin(int elementIndex = 0) =>
                throw new NotSupportedException("The analyzer test supports span access only.");
            public override void Unpin() { }
            protected override void Dispose(bool disposing) { }
        }
    }
}
