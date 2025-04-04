using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Nethermind.Evm;
using Nethermind.StatsAnalyzer.Plugin.Analyzer;
using NUnit.Framework;

namespace Nethermind.StatsAnalyzer.Plugin.Test;

[TestFixture]
public class NGramTests
{
    public static IEnumerable<TestCaseData> SubsequenceTestCases
    {
        get
        {
            yield return new TestCaseData(new[] { Instruction.POP },
                    new Instruction[][]
                    {
                    })
                .SetName("OneGramTest");
            yield return new TestCaseData(new[] { Instruction.POP, Instruction.POP },
                    new Instruction[][]
                    {
                        [Instruction.POP, Instruction.POP]
                    })
                .SetName("TwoGramTest");
            yield return new TestCaseData(new[] { Instruction.POP, Instruction.POP, Instruction.ADD },
                    new Instruction[][]
                    {
                        [Instruction.POP, Instruction.ADD],
                        [Instruction.POP, Instruction.POP, Instruction.ADD]
                    })
                .SetName("ThreeGramTest");
            yield return new TestCaseData(
                    new[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL },
                    new Instruction[][]
                    {
                        [Instruction.ADD, Instruction.MUL],
                        [Instruction.POP, Instruction.ADD, Instruction.MUL],
                        [Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL]
                    })
                .SetName("FourGramTest");
            yield return new TestCaseData(
                    new[]
                        { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV },
                    new Instruction[][]
                    {
                        [Instruction.MUL, Instruction.DIV],
                        [Instruction.ADD, Instruction.MUL, Instruction.DIV],
                        [Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV],
                        [Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV]
                    })
                .SetName("FiveGramTest");
            yield return new TestCaseData(
                    new[]
                    {
                        Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV,
                        Instruction.SUB
                    },
                    new Instruction[][]
                    {
                        [Instruction.DIV, Instruction.SUB],
                        [Instruction.MUL, Instruction.DIV, Instruction.SUB],
                        [Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB],
                        [Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB],
                        [
                            Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV,
                            Instruction.SUB
                        ]
                    })
                .SetName("SixGramTest");
            yield return new TestCaseData(
                    new[]
                    {
                        Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV,
                        Instruction.SUB, Instruction.NOT
                    },
                    new Instruction[][]
                    {
                        [Instruction.SUB, Instruction.NOT],
                        [Instruction.DIV, Instruction.SUB, Instruction.NOT],
                        [Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT],
                        [Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT],
                        [
                            Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB,
                            Instruction.NOT
                        ],
                        [
                            Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV,
                            Instruction.SUB, Instruction.NOT
                        ]
                    })
                .SetName("SevenGramTest");
        }
    }


    [TestCase("POP POP ADD MUL DIV SUB NOT",
        new[]
        {
            Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB,
            Instruction.NOT
        })]
    [TestCase("GT LT DIV", new[] { Instruction.GT, Instruction.LT, Instruction.DIV })]
    [TestCase("ADD DIV", new[] { Instruction.ADD, Instruction.DIV })]
    public void validate_ngram_string_conversion(string str, Instruction[] testcase)
    {
        var ngram = new NGram(testcase);
        var instructions = ngram.ToString();
        Assert.That(instructions == str, $" expected {str} found {instructions}");
    }

    [TestCase(new[] { Instruction.EQ, Instruction.MUL, Instruction.POP, Instruction.DIV })]
    [TestCase(new[] { Instruction.POP, Instruction.POP, Instruction.DIV })]
    [TestCase(new[] { Instruction.ADD, Instruction.DIV })]
    public void validate_ngram_byte_conversion(Instruction[] testcase)
    {
        var ngram = new NGram(testcase);
        var instructions = ngram.ToBytes();
        Assert.That(instructions.Length == testcase.Length);
        for (var i = 0; i < testcase.Length; i++)
            Assert.That(instructions[i] == (byte)testcase[i]);
    }


    [TestCase(
        new[]
        {
            Instruction.MUL, Instruction.POP, Instruction.DIV, Instruction.EQ, Instruction.MUL, Instruction.POP,
            Instruction.DIV
        }, Instruction.PUSH1)]
    [TestCase(new[] { Instruction.EQ, Instruction.MUL, Instruction.POP, Instruction.DIV },
        Instruction.PUSH1)]
    [TestCase(new[] { Instruction.POP, Instruction.POP, Instruction.DIV }, Instruction.POP)]
    [TestCase(new[] { Instruction.ADD, Instruction.DIV }, Instruction.MUL)]
    public void validate_ngram_shift_add_op(Instruction[] instructions, Instruction instruction)
    {
        var ngram = new NGram(instructions);
        ngram = ngram.ShiftAdd(instruction);
        var _instructions = ngram.ToInstructions();
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


    [Test]
    [TestCaseSource(nameof(SubsequenceTestCases))]
    public void validate_ngram_subsequece_generation(Instruction[] testcase, Instruction[][] expectedSubsequences)
    {
        var counts = new Dictionary<ulong, ulong>();
        var ngram = new NGram(testcase);


        Assert.That(ngram.GetSubsequences().Count() == expectedSubsequences.Count(),
            $"Found: {ngram.GetSubsequences().Count()}, Expected: {expectedSubsequences.Count()}");

        var expectedSubsequence = 0;

        foreach (var subSequence in ngram.GetSubsequences())
        {
            var expected = new NGram(expectedSubsequences[expectedSubsequence]);
            Assert.That(subSequence == expected,
                $"Expected sub-sequence {expected.ToString()} found {subSequence.ToString()}");
            expectedSubsequence++;
        }
    }

    [Test]
    [TestCaseSource(nameof(SubsequenceTestCases))]
    public unsafe void validate_ngram_subsequece_processing(Instruction[] testcase,
        Instruction[][] expectedSubsequences)
    {
        var sketchBuffer = new CmSketch[1];
        var topNMap = new Dictionary<ulong, ulong>();
        var topNQueue = new PriorityQueue<ulong, ulong>();
        var ngram = new NGram(testcase);

        static ulong CollectSubsequence(ulong subNgram, int currentSketchPos, int bufferSize,
            ulong minSupport, ulong max, int topN, CmSketch[] sketchBuffer, Dictionary<ulong, ulong> topNMap,
            PriorityQueue<ulong, ulong> topNQueue)
        {
            topNMap[subNgram] = 1 + CollectionsMarshal.GetValueRefOrAddDefault(topNMap, subNgram, out _);
            return 0;
        }

        delegate*<ulong, int, int, ulong, ulong, int, CmSketch[], Dictionary<ulong, ulong>, PriorityQueue<ulong, ulong>,
            ulong> action = &CollectSubsequence;

        ulong minSupport = 0;
        NGram.ProcessEachSubsequence(ngram, action, 0, 0, minSupport, 0, 0, sketchBuffer, topNMap, topNQueue);

        Assert.That(expectedSubsequences.Length == topNMap.Count,
            $"Expected: {expectedSubsequences.Length}, Found: {topNMap.Count}");

        foreach (var expectedArray in expectedSubsequences)
        {
            var expectedNGram = new NGram(expectedArray);
            var expectedHash = expectedNGram.Ulong0;
            Assert.That(topNMap.ContainsKey(expectedHash),
                $"Expected NGram {expectedNGram.ToString()} was not processed.");
        }
    }


    [Test]
    public void validate_ngrams_reset()
    {
        var ngrams = new NGram();
        ngrams = ngrams.ShiftAdd(Instruction.PUSH1);
        ngrams = ngrams.ShiftAdd(Instruction.PUSH1);
        Assert.That(ngrams.Ulong0 != NGram.Null);
        ngrams = ngrams.ShiftAdd(NGram.Reset);
        Assert.That(ngrams.Ulong0 == NGram.Null,
            $"Failed Reset Test found value {ngrams.Ulong0} expected {NGram.Null}");
    }
}
