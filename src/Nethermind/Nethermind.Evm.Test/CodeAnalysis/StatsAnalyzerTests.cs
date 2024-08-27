// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Intrinsics;

using FluentAssertions;
using Nethermind.Evm.CodeAnalysis;
using NUnit.Framework;

namespace Nethermind.Evm.Test.CodeAnalysis
{
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
        public void validate_ngram_generation(Instruction[] transaction, (Instruction[] ngram, int count)[] ngrams)
        {
            Console.WriteLine($"entering test length transaction : {transaction.Length} , ngrams: {ngrams.Count()}");
            StatsAnalyzer.GetInstance(100, transaction.Length, "");
            foreach (Instruction instruction in transaction)
            {
                StatsAnalyzer.AddInstruction(instruction);
            }
            StatsAnalyzer.NoticeTransactionCompletion();
            StatsAnalyzer.NoticeBlockCompletionBlocking();
            StatsAnalyzer.NoticeBlockCompletionBlocking();
            Assert.That(StatsAnalyzer.Count == ngrams.Count(), $" Total ngrams expected {ngrams.Count()}, found {StatsAnalyzer.Count}");
            foreach ((Instruction[] ngram, int expectedCount) ngramAndCount in ngrams)
            {
                Assert.That(StatsAnalyzer.GetStatInfo(ngramAndCount.ngram).Count == ngramAndCount.expectedCount, $"{StatsAnalyzer.AsNGram(ngramAndCount.ngram)} count: {StatsAnalyzer.GetStatInfo(ngramAndCount.ngram).Count}, expected Count: {ngramAndCount.expectedCount}");
                Console.WriteLine($"stats {StatsAnalyzer.GetStatInfo(ngramAndCount.ngram)}, expected Count: {ngramAndCount.expectedCount}");
            }

            StatsAnalyzer.Reset();

        }

    }
}
