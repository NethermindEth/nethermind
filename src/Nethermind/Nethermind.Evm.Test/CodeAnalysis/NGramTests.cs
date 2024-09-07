
using System;
using System.Collections.Generic;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.CodeAnalysis.StatsAnalyzer;
using NUnit.Framework;

namespace Nethermind.Evm.Test.CodeAnalysis
{
    [TestFixture]
    public class NGramTests
    {
        public static IEnumerable<TestCaseData> NGramIterationTestCases
        {
            get
            {
                yield return new TestCaseData(new Instruction[] { Instruction.POP },
                        new (Instruction[], int)[] {
                        (new Instruction[] { }, 1)
                        })
                    .SetName("OneGramTest");
                yield return new TestCaseData(new Instruction[] { Instruction.POP, Instruction.POP },
                        new (Instruction[], int)[] {
                        (new Instruction[] { Instruction.POP, Instruction.POP }, 1)
                        })
                    .SetName("TwoGramTest");
                yield return new TestCaseData(new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD },
                        new (Instruction[], int)[] {
                        (new Instruction[] { Instruction.POP, Instruction.ADD }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD }, 1)
                        })
                    .SetName("ThreeGramTest");
                yield return new TestCaseData(new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL },
                        new (Instruction[], int)[] {
                        (new Instruction[] { Instruction.ADD, Instruction.MUL }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.ADD, Instruction.MUL }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL }, 1)
                        })
                    .SetName("FourGramTest");
                yield return new TestCaseData(new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV },
                        new (Instruction[], int)[] {
                        (new Instruction[] { Instruction.MUL, Instruction.DIV }, 1),
                        (new Instruction[] { Instruction.ADD, Instruction.MUL, Instruction.DIV }, 1),
                        (new Instruction[] {  Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV}, 1),
                        (new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV }, 1),
                        })
                    .SetName("FiveGramTest");
                yield return new TestCaseData(new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB },
                        new (Instruction[], int)[] {
                        (new Instruction[] {  Instruction.DIV, Instruction.SUB }, 1),
                        (new Instruction[] {  Instruction.MUL, Instruction.DIV, Instruction.SUB }, 1),
                        (new Instruction[] {  Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB}, 1),
                        (new Instruction[] { Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB }, 1),
                        })
                    .SetName("SixGramTest");
                yield return new TestCaseData(new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT },
                        new (Instruction[], int)[] {
                        (new Instruction[] {  Instruction.SUB, Instruction.NOT }, 1),
                        (new Instruction[] {  Instruction.DIV, Instruction.SUB, Instruction.NOT }, 1),
                        (new Instruction[] {  Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT}, 1),
                        (new Instruction[] { Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT }, 1),
                        (new Instruction[] { Instruction.POP, Instruction.POP, Instruction.ADD, Instruction.MUL, Instruction.DIV, Instruction.SUB, Instruction.NOT }, 1),
                        })
                    .SetName("SevenGramTest");
            }
        }


        [Test, TestCaseSource(nameof(NGramIterationTestCases))]
        public void validate_ngram_iteration(Instruction[] testcase, (Instruction[] gram, int count)[] ngrams)
        {
            Dictionary<ulong, ulong> counts = new Dictionary<ulong, ulong>();
            NGram ngram = new NGram(testcase);


            foreach ((Instruction[] _ngram, int expectedCount) gram in ngrams)
                counts[new NGram(gram._ngram).ngram] = (ulong)gram.expectedCount;

            var ngramCount = 0;

            foreach (ulong _ngram in ngram)
            {
                --counts[_ngram];
                ++ngramCount;
                if (counts[_ngram] == 0) counts.Remove(_ngram);
            }

            if (testcase.Length > 1)
                Assert.That(counts.Count == 0 && ngrams.Length == ngramCount,
                        $" {counts.Count} ngrams generated are different from the ones given");

            if (testcase.Length <= 1)
                Assert.That(ngramCount == 0,
                        $" Expected 0 ngrams to be iterated on the given instruction array of length {testcase.Length}");
        }
    }
}
