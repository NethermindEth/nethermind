
using System;
using System.Collections.Generic;
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
                counts[new NGrams(_ngram).ngram] = 1;

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
    }
}
