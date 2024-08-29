// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Intrinsics;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.CodeAnalysis.IL;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test.CodeAnalysis
{
    public class DynamicInstructionChunk : InstructionChunk
    {
        public byte[] Pattern { get; private set; }

        public byte[] InstancePattern { get; private set; }
        public byte CallCount { get; private set; } = 0;

        public DynamicInstructionChunk(byte[] pattern)
        {
            Pattern = pattern;
        }


        void InstructionChunk.Invoke<T>(EvmState vmState, IWorldState worldState, IReleaseSpec spec, ref int programCounter, ref long gasAvailable, ref EvmStack<T> stack)
        {
            CallCount++;
        }
    }

    public record struct TransactionData(byte[] ByteCode, byte[] ExecutionCode);

    public static class LargeByteCodeBuilder
    {
        public const string P01P01ADD = "P01P01ADD";
        public const string P01P01MUL = "P01P01MUL";
        public const string P01P01SUB = "P01P01SUB";
        public const string P01P01DIV = "P01P01DIV";
        public const string FILLER = "FILLER";

        private static Prepare p01p01Mul(Prepare p)
        {
            return p.PushSingle(2)
                    .PushSingle(3)
                    .ADD();
        }
        private static Prepare p01p01Add(Prepare p)
        {
            return p.PushSingle(2)
                    .PushSingle(3)
                    .ADD();
        }
        private static Prepare filler(Prepare p)
        {
            return p.NOT();
        }




        public static Prepare PreparePattern(string pattern, Prepare p)
        {
            switch (pattern)
            {
                case P01P01ADD: return p01p01Add(p);
                case P01P01MUL: return p01p01Mul(p);
                default:
                    throw new NotSupportedException($"error: a requested {pattern} is not implemented!");
            }
        }
        public static DynamicInstructionChunk PreparePatternChunk(string pattern)
        {

            return new DynamicInstructionChunk(PrepareTransaction(new (string, int)[] { (pattern, 1) }).ExecutionCode);
        }


        public static TransactionData PrepareTransaction((string, int)[] patternAndCount)
        {

            Prepare p = Prepare.EvmCode;
            foreach ((string pattern, int count) in patternAndCount)
            {
                for (int i = 0; i < count; i++)
                {
                    PreparePattern(pattern, p);

                }
            }
            byte[] code = p.Done;
            OpcodeInfo[] strippedCode = IlAnalyzer.StripByteCode(new ReadOnlySpan<byte>(code)).Item1;
            byte[] b = new byte[strippedCode.Length];
            for (int i = 0; i < strippedCode.Length; i++)
            {
                b[i] = (byte)strippedCode[i].Operation;
                //Console.Write($"{(Instruction)b[i]}");
            }
            //Console.WriteLine("");
            return new TransactionData(p.Done, b);
        }
    }

    [TestFixture]
    public class StatsAnalyzerTests
    {
        public static IEnumerable<TestCaseData> NgramTestCases
        {
            get
            {
                yield return new TestCaseData(new Instruction[] { Instruction.POP, Instruction.ADD },
                        new (Instruction[], int)[] {
                        (new Instruction[] { Instruction.POP, Instruction.ADD }, 1)
                        })
                    .SetName("TwoInstructionsTest");
                yield return new TestCaseData(new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD },
                        new (Instruction[], int)[] {
                        (new Instruction[] { Instruction.POP, Instruction.ADD }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.POP }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD }, 1)
                        })
                    .SetName("ThreeInstructionsTest");
                yield return new TestCaseData(new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL },
                        new (Instruction[], int)[] {
                        (new Instruction[] { Instruction.POP, Instruction.ADD }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.POP }, 1),
                        (new Instruction[] { Instruction.ADD, Instruction.MUL }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.ADD, Instruction.MUL }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL }, 1)
                        })
                    .SetName("FourInstructionsTest");
                yield return new TestCaseData(new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV },
                        new (Instruction[], int)[] {
                        (new Instruction[] { Instruction.POP, Instruction.ADD }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.POP }, 1),
                        (new Instruction[] { Instruction.ADD, Instruction.MUL }, 1),
                        (new Instruction[] { Instruction.MUL, Instruction.DIV }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.ADD, Instruction.MUL }, 1),
                        (new Instruction[] { Instruction.ADD, Instruction.MUL, Instruction.DIV }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL }, 1),
                        (new Instruction[] {  Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV}, 1),
                        (new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV }, 1),
                        })
                    .SetName("FiveInstructionsTest");
                yield return new TestCaseData(new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB },
                        new (Instruction[], int)[] {
                        (new Instruction[] { Instruction.POP, Instruction.ADD }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.POP }, 1),
                        (new Instruction[] { Instruction.ADD, Instruction.MUL }, 1),
                        (new Instruction[] { Instruction.MUL, Instruction.DIV }, 1),
                        (new Instruction[] {  Instruction.DIV, Instruction.SUB }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.ADD, Instruction.MUL }, 1),
                        (new Instruction[] { Instruction.ADD, Instruction.MUL, Instruction.DIV }, 1),
                        (new Instruction[] {  Instruction.MUL, Instruction.DIV, Instruction.SUB }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL }, 1),
                        (new Instruction[] {  Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV}, 1),
                        (new Instruction[] {  Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB}, 1),
                        (new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB }, 1),
                        })
                    .SetName("SixInstructionsTest");
                yield return new TestCaseData(new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT },
                        new (Instruction[], int)[] {
                        (new Instruction[] { Instruction.POP, Instruction.ADD }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.POP }, 1),
                        (new Instruction[] { Instruction.ADD, Instruction.MUL }, 1),
                        (new Instruction[] { Instruction.MUL, Instruction.DIV }, 1),
                        (new Instruction[] {  Instruction.DIV, Instruction.SUB }, 1),
                        (new Instruction[] {  Instruction.SUB, Instruction.NOT }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.ADD, Instruction.MUL }, 1),
                        (new Instruction[] { Instruction.ADD, Instruction.MUL, Instruction.DIV }, 1),
                        (new Instruction[] {  Instruction.MUL, Instruction.DIV, Instruction.SUB }, 1),
                        (new Instruction[] {  Instruction.DIV, Instruction.SUB, Instruction.NOT }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL }, 1),
                        (new Instruction[] {  Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV}, 1),
                        (new Instruction[] {  Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB}, 1),
                        (new Instruction[] {  Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT}, 1),
                        (new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB }, 1),
                        (new Instruction[] { Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT }, 1),
                        })
                    .SetName("SevenInstructionsTest");
                yield return new TestCaseData(new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT, Instruction.EQ },
                        new (Instruction[], int)[] {
                        (new Instruction[] { Instruction.POP, Instruction.ADD }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.POP }, 1),
                        (new Instruction[] { Instruction.ADD, Instruction.MUL }, 1),
                        (new Instruction[] { Instruction.MUL, Instruction.DIV }, 1),
                        (new Instruction[] {  Instruction.DIV, Instruction.SUB }, 1),
                        (new Instruction[] {  Instruction.SUB, Instruction.NOT }, 1),
                        (new Instruction[] {  Instruction.NOT, Instruction.EQ }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.ADD, Instruction.MUL }, 1),
                        (new Instruction[] { Instruction.ADD, Instruction.MUL, Instruction.DIV }, 1),
                        (new Instruction[] {  Instruction.MUL, Instruction.DIV, Instruction.SUB }, 1),
                        (new Instruction[] {  Instruction.DIV, Instruction.SUB, Instruction.NOT }, 1),
                        (new Instruction[] {  Instruction.SUB, Instruction.NOT, Instruction.EQ }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL }, 1),
                        (new Instruction[] {  Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV}, 1),
                        (new Instruction[] {  Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB}, 1),
                        (new Instruction[] {  Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT}, 1),
                        (new Instruction[] {  Instruction.DIV, Instruction.SUB, Instruction.NOT, Instruction.EQ}, 1),
                        (new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB }, 1),
                        (new Instruction[] { Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT }, 1),
                        (new Instruction[] { Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT, Instruction.EQ }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT }, 1),
                        (new Instruction[] { Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT, Instruction.EQ }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT, Instruction.EQ }, 1),
                        })
                    .SetName("EightInstructionsTest");
            }
        }

        [Test, TestCaseSource(nameof(NgramTestCases))]
        public void validate_ngram_generation_exhaustive(Instruction[] transaction, (Instruction[] ngram, int count)[] ngrams)
        {
            StatsAnalyzer.Reset();
            //Console.WriteLine($"entering test length transaction : {transaction.Length} , ngrams: {ngrams.Count()}");
            StatsAnalyzer.GetInstance(100, transaction.Length, "");
            foreach (Instruction instruction in transaction)
            {
                StatsAnalyzer.AddInstruction(instruction);
            }
            StatsAnalyzer.NoticeTransactionCompletion();
            StatsAnalyzer.NoticeBlockCompletionBlocking();
            Assert.That(StatsAnalyzer.Count == ngrams.Count(), $" Total ngrams expected {ngrams.Count()}, found {StatsAnalyzer.Count}");
            foreach ((Instruction[] ngram, int expectedCount) ngramAndCount in ngrams)
            {
                Assert.That(StatsAnalyzer.GetStatInfo(ngramAndCount.ngram).Count == ngramAndCount.expectedCount, $"{StatsAnalyzer.AsNGram(ngramAndCount.ngram)} count: {StatsAnalyzer.GetStatInfo(ngramAndCount.ngram).Count}, expected Count: {ngramAndCount.expectedCount}");
                //Console.WriteLine($"stats {StatsAnalyzer.GetStatInfo(ngramAndCount.ngram)}, expected Count: {ngramAndCount.expectedCount}");
            }

            StatsAnalyzer.Reset();

        }

        public void PsuedoBlockExecution(byte[][] transactions)
        {
            foreach (byte[] transaction in transactions)
            {
                PsuedoTransactionExecution(transaction);
            }
            StatsAnalyzer.NoticeBlockCompletionBlocking();
        }

        public void PsuedoTransactionExecution(byte[] code)
        {

            // OpcodeInfo[] strippedCode = IlAnalyzer.StripByteCode(new ReadOnlySpan<byte>(code)).Item1;
            // byte[] b = new byte[strippedCode.Length];
            for (int i = 0; i < code.Length; i++)
            {
                StatsAnalyzer.AddInstruction((Instruction)code[i]);
            }
            StatsAnalyzer.NoticeTransactionCompletion();
        }

        public static IEnumerable<TestCaseData> BlockTestCase
        {
            get
            {
                yield return new TestCaseData(
                        new (string, int)[][] {
                        new (string, int)[] { (LargeByteCodeBuilder.P01P01MUL, 10) }
                        }, 2, true
                      )
                    .SetName("SingleTransactionBlocking");
                yield return new TestCaseData(
                        new (string, int)[][] {
                        new (string, int)[] { (LargeByteCodeBuilder.P01P01MUL, 10) }
                        }, 2, false
                      )
                    .SetName("SingleTransactionNonBlocking");
            }
        }

        [Test, TestCaseSource(nameof(BlockTestCase))]
        public void validate_ngram_count_large_pattern((string, int)[][] block, int timesTwo, bool blocking )
        {
            // Console.WriteLine("entering new test");
            StatsAnalyzer.Reset();
            StatsAnalyzer.GetInstance(100, 100000, "");
            TransactionData t;
            for (int j = 0; j < timesTwo * 2; j++)
            {
                // Console.WriteLine($"J: {j}");
                foreach ((string, int)[] trasaction in block)
                {

                    t = LargeByteCodeBuilder.PrepareTransaction(trasaction);
                    PsuedoTransactionExecution(t.ExecutionCode);
                }

               if (blocking){
                   // Console.WriteLine("calling notice block blocking");
                   StatsAnalyzer.NoticeBlockCompletionBlocking();
               } else {
                   // Console.WriteLine($"calling notice block non blocking at j {j}");
                   StatsAnalyzer.NoticeBlockCompletion();
               }

                if ((j != 0))
                {
                    // Console.WriteLine($"Asserting at j: {j}");

                   if(!blocking) Thread.Sleep(1000);
                    foreach ((string, int)[] trasaction in block)
                    {
                        foreach ((string pattern, int count) in trasaction)
                        {
                            DynamicInstructionChunk chunk = LargeByteCodeBuilder.PreparePatternChunk(pattern);
                            Assert.That(StatsAnalyzer.GetStatInfo(chunk.Pattern).Count == count * (j + 1), $" Total count expected {count * (j + 1)}, found {StatsAnalyzer.GetStatInfo(chunk.Pattern).Count} Stat: {StatsAnalyzer.GetStatInfo(chunk.Pattern)} ");
                            Assert.That(StatsAnalyzer.GetStatInfo(chunk.Pattern).Freq == count, $" Freq expected {count}, found {StatsAnalyzer.GetStatInfo(chunk.Pattern).Freq})");
                        }
                    }
                }
            }

            StatsAnalyzer.Reset();
        }
    }
}

