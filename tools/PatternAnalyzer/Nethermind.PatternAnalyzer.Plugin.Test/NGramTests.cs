using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Nethermind.Evm;
using Nethermind.PatternAnalyzer.Plugin.Analyzer;
using NUnit.Framework;

namespace Nethermind.PatternAnalyzer.Plugin.Test
{
    [TestFixture]
    public class NGramTests
    {
        public static IEnumerable<TestCaseData> SubsequenceTestCases
        {
            get
            {
                yield return new TestCaseData(new Instruction[] { Instruction.POP },
                        new Instruction[][] {
                        })
                    .SetName("OneGramTest");
                yield return new TestCaseData(new Instruction[] { Instruction.POP, Instruction.POP },
                        new Instruction[][] {
                            [Instruction.POP, Instruction.POP]
                        })
                    .SetName("TwoGramTest");
                yield return new TestCaseData(new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD },
                        new Instruction[][] {
                            [Instruction.POP, Instruction.ADD],
                            [Instruction.POP, Instruction.POP, Instruction.ADD]
                        })
                    .SetName("ThreeGramTest");
                yield return new TestCaseData(new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL },
                        new Instruction[][] {
                            [Instruction.ADD, Instruction.MUL],
                            [Instruction.POP, Instruction.ADD, Instruction.MUL],
                            [Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL]
                        })
                    .SetName("FourGramTest");
                yield return new TestCaseData(new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV },
                        new Instruction[][] {
                            [Instruction.MUL, Instruction.DIV],
                            [Instruction.ADD, Instruction.MUL, Instruction.DIV],
                            [Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV],
                            [Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV],
                        })
                    .SetName("FiveGramTest");
                yield return new TestCaseData(new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB },
                        new Instruction[][] {
                            [Instruction.DIV, Instruction.SUB],
                            [Instruction.MUL, Instruction.DIV, Instruction.SUB],
                            [Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB],
                            [Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB],
                            [Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB
                            ],
                        })
                    .SetName("SixGramTest");
                yield return new TestCaseData(new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT },
                        new Instruction[][] {
                            [Instruction.SUB, Instruction.NOT],
                            [Instruction.DIV, Instruction.SUB, Instruction.NOT],
                            [Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT],
                            [Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT],
                            [Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT
                            ],
                            [Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT
                            ],
                        })
                    .SetName("SevenGramTest");
            }
        }


        [TestCase("POP POP ADD MUL DIV SUB NOT", new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT })]
        [TestCase("GT LT DIV", new Instruction[] { Instruction.GT, Instruction.LT, Instruction.DIV })]
        [TestCase("ADD DIV", new Instruction[] { Instruction.ADD, Instruction.DIV })]
        public void validate_ngram_string_conversion(string str, Instruction[] testcase)
        {
            NGram ngram = new NGram(testcase);
            string instructions = ngram.ToString();
            Assert.That(instructions == str, $" expected {str} found {instructions}");
        }

        [TestCase(new Instruction[] { Instruction.EQ, Instruction.MUL, Instruction.POP, Instruction.DIV })]
        [TestCase(new Instruction[] { Instruction.POP, Instruction.POP, Instruction.DIV })]
        [TestCase(new Instruction[] { Instruction.ADD, Instruction.DIV })]
        public void validate_ngram_byte_conversion(Instruction[] testcase)
        {
            NGram ngram = new NGram(testcase);
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
            NGram ngram = new NGram(instructions);
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


        [Test, TestCaseSource(nameof(SubsequenceTestCases))]
        public void validate_ngram_subsequece_generation(Instruction[] testcase, Instruction[][] expectedSubsequences)
        {
            Dictionary<ulong, ulong> counts = new Dictionary<ulong, ulong>();
            NGram ngram = new NGram(testcase);


            Assert.That(ngram.GetSubsequences().Count() == expectedSubsequences.Count(),
                    $"Found: {ngram.GetSubsequences().Count()}, Expected: {expectedSubsequences.Count()}");

            var expectedSubsequence = 0;

            foreach (NGram subSequence in ngram.GetSubsequences())
            {
                var expected = new NGram(expectedSubsequences[expectedSubsequence]);
                Assert.That(subSequence == expected ,
                        $"Expected sub-sequence {expected.ToString()} found {subSequence.ToString()}");
                expectedSubsequence++;

            }
        }


    //    public void validate_ngrams_process_instructions(Instruction[] executionOpCodes, (Instruction[] ngram, int count)[] expectedNGrams)
    //    {
    //        Dictionary<ulong, ulong> counts = new Dictionary<ulong, ulong>();
    //        Action<ulong> CountNGrams = (ulong ngram) =>
    //                                               {
    //                                                   counts[ngram] = 1 + CollectionsMarshal.GetValueRefOrAddDefault(counts, ngram, out bool _);
    //                                               };
    //        NGram Ngram = new NGram();
    //        Ngram = NGram.ProcessInstructions(executionOpCodes, Ngram, CountNGrams);

    //        foreach ((Instruction[] ngram, int expectedCount) expected in expectedNGrams)
    //        {

    //            NGram currentNGram = new NGram(expected.ngram);
    //            Assert.That(counts.ContainsKey(currentNGram.ulong0),
    //                    $"{currentNGram.ToString()} not present in testCase ");
    //            Assert.That(counts[currentNGram.ulong0] == (ulong)expected.expectedCount,
    //                                           $"Counts mismatch for {currentNGram.ToString()} expected {expected.expectedCount} found {counts[currentNGram.ulong0]}");
    //        }

    //    }


        [Test]
        public void validate_ngrams_reset()
        {
            NGram ngrams = new NGram();
            ngrams = ngrams.ShiftAdd(Instruction.PUSH1);
            ngrams = ngrams.ShiftAdd(Instruction.PUSH1);
            Assert.That(ngrams.ulong0 != NGram.NULL);
            ngrams = ngrams.ShiftAdd(NGram.RESET);
            Assert.That(ngrams.ulong0 == NGram.NULL, $"Failed Reset Test found value {ngrams.ulong0} expected {NGram.NULL}");
        }
    }
}
