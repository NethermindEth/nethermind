using System.Collections.Generic;
using Nethermind.Evm;
using Nethermind.StatsAnalyzer.Plugin.Analyzer;
using NUnit.Framework;

namespace Nethermind.StatsAnalyzer.Plugin.Test;

[TestFixture]
public class PatternStatsAnalyzerTests
{
    [SetUp]
    public void SetUp()
    {
        var sketch = new CmSketchBuilder().SetBuckets(1000).SetHashFunctions(4).Build();
        _patternStatsAnalyzer = new StatsAnalyzerBuilder().SetBufferSizeForSketches(2).SetTopN(100).SetCapacity(100000)
            .SetMinSupport(1).SetSketchResetOrReuseThreshold(0.001).SetSketch(sketch).Build();

        var sketch2 = new CmSketchBuilder().SetBuckets(1000).SetHashFunctions(4).Build();
        _patternStatsAnalyzerIgnore = new StatsAnalyzerBuilder().SetBufferSizeForSketches(2).SetTopN(100)
            .SetCapacity(100000)
            .SetMinSupport(1).SetSketchResetOrReuseThreshold(0.001).SetSketch(sketch2).Build();
    }

    private PatternStatsAnalyzer _patternStatsAnalyzer;
    private PatternStatsAnalyzer _patternStatsAnalyzerIgnore;
    private HashSet<Instruction> _ignoreSet = new() { Instruction.JUMPDEST, Instruction.JUMP };

    public static IEnumerable<TestCaseData> StatsTestCases
    {
        get
        {
            yield return new TestCaseData(new[] { Instruction.POP, Instruction.ADD },
                    new (Instruction[], int)[]
                    {
                        ([Instruction.POP, Instruction.ADD], 1)
                    })
                .SetName("TwoInstructionsTest");
            yield return new TestCaseData(new[] { Instruction.POP, Instruction.POP, Instruction.POP },
                    new (Instruction[], int)[]
                    {
                        ([Instruction.POP, Instruction.POP], 2),
                        ([Instruction.POP, Instruction.POP, Instruction.POP], 1)
                    })
                .SetName("ThreeInstructionsTest");
            yield return new TestCaseData(new[] { Instruction.POP, Instruction.POP, Instruction.ADD },
                    new (Instruction[], int)[]
                    {
                        ([Instruction.POP, Instruction.ADD], 1),
                        ([Instruction.POP, Instruction.POP], 1),
                        ([Instruction.POP, Instruction.POP, Instruction.ADD], 1)
                    })
                .SetName("ThreeInstructionsTest-2");
            yield return new TestCaseData(
                    new[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL },
                    new (Instruction[], int)[]
                    {
                        ([Instruction.POP, Instruction.ADD], 1),
                        ([Instruction.POP, Instruction.POP], 1),
                        ([Instruction.ADD, Instruction.MUL], 1),
                        ([Instruction.POP, Instruction.POP, Instruction.ADD], 1),
                        ([Instruction.POP, Instruction.ADD, Instruction.MUL], 1),
                        ([Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL], 1)
                    })
                .SetName("FourInstructionsTest");
            yield return new TestCaseData(
                    new[]
                        { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV },
                    new (Instruction[], int)[]
                    {
                        ([Instruction.POP, Instruction.ADD], 1),
                        ([Instruction.POP, Instruction.POP], 1),
                        ([Instruction.ADD, Instruction.MUL], 1),
                        ([Instruction.MUL, Instruction.DIV], 1),
                        ([Instruction.POP, Instruction.POP, Instruction.ADD], 1),
                        ([Instruction.POP, Instruction.ADD, Instruction.MUL], 1),
                        ([Instruction.ADD, Instruction.MUL, Instruction.DIV], 1),
                        ([Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL], 1),
                        ([Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV], 1),
                        ([Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV], 1)
                    })
                .SetName("FiveInstructionsTest");
            yield return new TestCaseData(
                    new[]
                    {
                        Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV,
                        Instruction.SUB
                    },
                    new (Instruction[], int)[]
                    {
                        ([Instruction.POP, Instruction.ADD], 1),
                        ([Instruction.POP, Instruction.POP], 1),
                        ([Instruction.ADD, Instruction.MUL], 1),
                        ([Instruction.MUL, Instruction.DIV], 1),
                        ([Instruction.DIV, Instruction.SUB], 1),
                        ([Instruction.POP, Instruction.POP, Instruction.ADD], 1),
                        ([Instruction.POP, Instruction.ADD, Instruction.MUL], 1),
                        ([Instruction.ADD, Instruction.MUL, Instruction.DIV], 1),
                        ([Instruction.MUL, Instruction.DIV, Instruction.SUB], 1),
                        ([Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL], 1),
                        ([Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV], 1),
                        ([Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB], 1),
                        ([Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV], 1),
                        ([Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB], 1),
                        ([Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB],
                            1)
                    })
                .SetName("SixInstructionsTest");
            yield return new TestCaseData(
                    new[]
                    {
                        Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV,
                        Instruction.SUB, Instruction.NOT
                    },
                    new (Instruction[], int)[]
                    {
                        ([Instruction.POP, Instruction.ADD], 1),
                        ([Instruction.POP, Instruction.POP], 1),
                        ([Instruction.ADD, Instruction.MUL], 1),
                        ([Instruction.MUL, Instruction.DIV], 1),
                        ([Instruction.DIV, Instruction.SUB], 1),
                        ([Instruction.SUB, Instruction.NOT], 1),
                        ([Instruction.POP, Instruction.POP, Instruction.ADD], 1),
                        ([Instruction.POP, Instruction.ADD, Instruction.MUL], 1),
                        ([Instruction.ADD, Instruction.MUL, Instruction.DIV], 1),
                        ([Instruction.MUL, Instruction.DIV, Instruction.SUB], 1),
                        ([Instruction.DIV, Instruction.SUB, Instruction.NOT], 1),
                        ([Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL], 1),
                        ([Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV], 1),
                        ([Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB], 1),
                        ([Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT], 1),
                        ([Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV], 1),
                        ([Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB], 1),
                        ([Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT], 1),
                        ([Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB],
                            1),
                        ([Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT],
                            1),
                        ([Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT],
                            1)
                    })
                .SetName("SevenInstructionsTest");
            yield return new TestCaseData(
                    new[]
                    {
                        Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV,
                        Instruction.SUB, Instruction.NOT, Instruction.EQ
                    },
                    new (Instruction[], int)[]
                    {
                        ([Instruction.POP, Instruction.ADD], 1),
                        ([Instruction.POP, Instruction.POP], 1),
                        ([Instruction.ADD, Instruction.MUL], 1),
                        ([Instruction.MUL, Instruction.DIV], 1),
                        ([Instruction.DIV, Instruction.SUB], 1),
                        ([Instruction.SUB, Instruction.NOT], 1),
                        ([Instruction.NOT, Instruction.EQ], 1),
                        ([Instruction.POP, Instruction.POP, Instruction.ADD], 1),
                        ([Instruction.POP, Instruction.ADD, Instruction.MUL], 1),
                        ([Instruction.ADD, Instruction.MUL, Instruction.DIV], 1),
                        ([Instruction.MUL, Instruction.DIV, Instruction.SUB], 1),
                        ([Instruction.DIV, Instruction.SUB, Instruction.NOT], 1),
                        ([Instruction.SUB, Instruction.NOT, Instruction.EQ], 1),
                        ([Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL], 1),
                        ([Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV], 1),
                        ([Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB], 1),
                        ([Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT], 1),
                        ([Instruction.DIV, Instruction.SUB, Instruction.NOT, Instruction.EQ], 1),
                        ([Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV], 1),
                        ([Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB], 1),
                        ([Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT], 1),
                        ([Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT, Instruction.EQ], 1),
                        ([Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB],
                            1),
                        ([Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT],
                            1),
                        ([Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT, Instruction.EQ],
                            1),
                        ([Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT],
                            1),
                        ([Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT, Instruction.EQ],
                            1)
                    })
                .SetName("EightInstructionsTest");
        }
    }

    [Test]
    [TestCaseSource(nameof(StatsTestCases))]
    public void validate_stats(Instruction[] executionOpCodes,
        (Instruction[] ngram, int count)[] expectedNGrams)
    {
        var counts = new Dictionary<ulong, ulong>();

        foreach ((Instruction[] instructions, int count) expected in expectedNGrams)
        {
            var ngram = new NGram(expected.instructions);
            counts[ngram.Ulong0] = (ulong)expected.count;
        }

        _patternStatsAnalyzer.Add(executionOpCodes);

        foreach (var stat in _patternStatsAnalyzer.Stats)
        {
            var ulong0 = stat.Ngram.Ulong0;
            Assert.That(counts[ulong0] == stat.Count);
            counts.Remove(ulong0);
        }

        Assert.That(counts.Count == 0);
    }
}
