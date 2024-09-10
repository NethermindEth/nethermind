
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.CodeAnalysis.StatsAnalyzer;
using NUnit.Framework;

namespace Nethermind.Evm.Test.CodeAnalysis
{
    [TestFixture]
    public class NGramsTests
    {
        public static IEnumerable<TestCaseData> NGramsIterationTestCases
        {
            get
            {
                yield return new TestCaseData(new Instruction[] { Instruction.POP },
                        new Instruction[][] {
                        new Instruction[] { }
                        })
                    .SetName("OneGramTest");
                yield return new TestCaseData(new Instruction[] { Instruction.POP, Instruction.POP },
                        new Instruction[][] {
                        new Instruction[] { Instruction.POP, Instruction.POP }
                        })
                    .SetName("TwoGramTest");
                yield return new TestCaseData(new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD },
                        new Instruction[][] {
                        new Instruction[] { Instruction.POP, Instruction.ADD },
                        new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD }
                        })
                    .SetName("ThreeGramTest");
                yield return new TestCaseData(new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL },
                        new Instruction[][] {
                        new Instruction[] { Instruction.ADD, Instruction.MUL },
                        new Instruction[] { Instruction.POP, Instruction.ADD, Instruction.MUL },
                        new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL }
                        })
                    .SetName("FourGramTest");
                yield return new TestCaseData(new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV },
                        new Instruction[][] {
                        new Instruction[] { Instruction.MUL, Instruction.DIV },
                        new Instruction[] { Instruction.ADD, Instruction.MUL, Instruction.DIV },
                        new Instruction[] {  Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV},
                        new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV },
                        })
                    .SetName("FiveGramTest");
                yield return new TestCaseData(new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB },
                        new Instruction[][] {
                        new Instruction[] {  Instruction.DIV, Instruction.SUB },
                        new Instruction[] {  Instruction.MUL, Instruction.DIV, Instruction.SUB },
                        new Instruction[] {  Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB},
                        new Instruction[] { Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB },
                        new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB },
                        })
                    .SetName("SixGramTest");
                yield return new TestCaseData(new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT },
                        new Instruction[][] {
                        new Instruction[] {  Instruction.SUB, Instruction.NOT },
                        new Instruction[] {  Instruction.DIV, Instruction.SUB, Instruction.NOT },
                        new Instruction[] {  Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT},
                        new Instruction[] { Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT },
                        new Instruction[] { Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT },
                        new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT },
                        })
                    .SetName("SevenGramTest");
            }
        }


        [TestCase("POP POP ADD MUL DIV SUB NOT", new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT })]
        [TestCase("GT LT DIV", new Instruction[] { Instruction.GT, Instruction.LT, Instruction.DIV })]
        [TestCase("ADD DIV", new Instruction[] { Instruction.ADD, Instruction.DIV })]
        public void validate_ngram_string_conversion(string str, Instruction[] testcase)
        {
            NGrams ngram = new NGrams(testcase);
            string instructions = ngram.ToString();
            Assert.That(instructions == str, $" expected {str} found {instructions}");
        }

        [TestCase(new Instruction[] { Instruction.EQ, Instruction.MUL, Instruction.POP, Instruction.DIV })]
        [TestCase(new Instruction[] { Instruction.POP, Instruction.POP, Instruction.DIV })]
        [TestCase(new Instruction[] { Instruction.ADD, Instruction.DIV })]
        public void validate_ngram_byte_conversion(Instruction[] testcase)
        {
            NGrams ngram = new NGrams(testcase);
            byte[] instructions = ngram.ToBytes();
            Assert.That(instructions.Length == testcase.Length);
            for (int i = 0; i < testcase.Length; i++)
                Assert.That(instructions[i] == (byte)testcase[i]);
        }


        [TestCase(new Instruction[] { Instruction.MUL, Instruction.POP, Instruction.DIV, Instruction.EQ, Instruction.MUL, Instruction.POP, Instruction.DIV }, Instruction.PUSH1)]
        [TestCase(new Instruction[] { Instruction.EQ, Instruction.MUL, Instruction.POP, Instruction.DIV }, Instruction.PUSH1)]
        [TestCase(new Instruction[] { Instruction.POP, Instruction.POP, Instruction.DIV }, Instruction.POP)]
        [TestCase(new Instruction[] { Instruction.ADD, Instruction.DIV }, Instruction.MUL)]
        public void validate_ngram_shift_add_op(Instruction[] instructions, Instruction instruction)
        {
            NGrams ngram = new NGrams(instructions);
            ngram = ngram.ShiftAdd(instruction);
            Instruction[] _instructions = ngram.ToInstructions();
            if (instructions.Length < 7)
            {
                Assert.That(_instructions[0] == instructions[0]);
                Assert.That(_instructions.Length == instructions.Length + 1);
            }
            else
            {
                Assert.That(_instructions[0] == instructions[1]);
                Assert.That(_instructions.Length == instructions.Length);
            }
        }


        [Test, TestCaseSource(nameof(NGramsIterationTestCases))]
        public void validate_ngram_iteration(Instruction[] testcase, Instruction[][] expectedNGrams)
        {
            Dictionary<ulong, ulong> counts = new Dictionary<ulong, ulong>();
            NGrams ngrams = new NGrams(testcase);


            foreach (Instruction[] _ngram in expectedNGrams)
                counts[new NGrams(_ngram).ulong0] = 1;

            var ngramCount = 0;

            foreach (ulong _ngram in ngrams)
            {
                --counts[_ngram];
                ++ngramCount;
                if (counts[_ngram] == 0) counts.Remove(_ngram);
            }

            if (testcase.Length > 1)
                Assert.That(counts.Count == 0 && expectedNGrams.Length == ngramCount,
                        $" {counts.Count} ngrams generated are different from the ones given");

            if (testcase.Length <= 1)
                Assert.That(ngramCount == 0,
                        $" Expected 0 ngrams to be iterated on the given instruction array of length {testcase.Length}");
        }


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
        public void validate_ngrams_process_instructions(Instruction[] executionOpCodes, (Instruction[] ngram, int count)[] expectedNGrams)
        {
            Dictionary<ulong, ulong> counts = new Dictionary<ulong, ulong>();
            Action<ulong> CountNGrams = (ulong ngram) =>
                                                   {
                                                       counts[ngram] = 1 + CollectionsMarshal.GetValueRefOrAddDefault(counts, ngram, out bool _);
                                                   };
            NGrams ngrams = new NGrams();
            ngrams = NGrams.ProcessInstructions(executionOpCodes, ngrams, CountNGrams);

            foreach ((Instruction[] ngram, int expectedCount) expected in expectedNGrams)
            {

                NGrams currentNGram = new NGrams(expected.ngram);
                Assert.That(counts.ContainsKey(currentNGram.ulong0),
                        $"{currentNGram.ToString()} not present in testCase ");
                Assert.That(counts[currentNGram.ulong0] == (ulong)expected.expectedCount,
                                               $"Counts mismatch for {currentNGram.ToString()} expected {expected.expectedCount} found {counts[currentNGram.ulong0]}");
            }

        }


        [Test]
        public void validate_ngrams_reset()
        {
            NGrams ngrams = new NGrams();
            ngrams = ngrams.ShiftAdd(Instruction.PUSH1);
            ngrams = ngrams.ShiftAdd(Instruction.PUSH1);
            Assert.That(ngrams.ulong0 != NGrams.NULL);
            ngrams = ngrams.ShiftAdd(NGrams.RESET);
            Assert.That(ngrams.ulong0 == NGrams.NULL, $"Failed Reset Test found value {ngrams.ulong0} expected {NGrams.NULL}");

            Dictionary<ulong, ulong> countsP01P01RESETP01P01 = new Dictionary<ulong, ulong>();
            Dictionary<ulong, ulong> countsP01P01P01P01 = new Dictionary<ulong, ulong>();
            Instruction[] P01P01 = new Instruction[] { Instruction.PUSH1, Instruction.PUSH1 };
            NGrams NGramsP01P01 = new NGrams(P01P01);
            Instruction[] P01P01RESETP01P01 = new Instruction[] { Instruction.PUSH1, Instruction.PUSH1, NGrams.RESET, Instruction.PUSH1, Instruction.PUSH1 };
            Instruction[] P01P01P01P01 = new Instruction[] { Instruction.PUSH1, Instruction.PUSH1, Instruction.PUSH1, Instruction.PUSH1 };
            NGrams.GetCounts(P01P01P01P01, countsP01P01P01P01);
            NGrams.GetCounts(P01P01RESETP01P01, countsP01P01RESETP01P01);
            countsP01P01P01P01.TryGetValue(NGramsP01P01.ulong0, out ulong P01P01count);
            Assert.That(P01P01count == 3,
                                $"expected {NGramsP01P01.ToString()} to have count 3 found {P01P01count} ");
            countsP01P01RESETP01P01.TryGetValue(NGramsP01P01.ulong0, out P01P01count);
            Assert.That(P01P01count == 2,
                                $"expected {NGramsP01P01.ToString()} to have count 3 found {P01P01count} ");
        }
    }
}
