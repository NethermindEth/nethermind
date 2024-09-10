// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Evm.CodeAnalysis.StatsAnalyzer;
using NUnit.Framework;

namespace Nethermind.Evm.Test.CodeAnalysis
{
    [TestFixture]
    public class StatsAnalyzerTests
    {

        [TestCase(8, 9)]
        [TestCase(2, 2)]
        public void validate_top_n(int patternQty, int maxRepetitionsPerPattern)
        {
            (List<Instruction> instructionBuffer, Dictionary<ulong, ulong> observedCounts) = RandNGrams(patternQty, true, maxRepetitionsPerPattern);
            Dictionary<ulong, HashSet<ulong>> countToSequences = new Dictionary<ulong, HashSet<ulong>>();
            StatsAnalyzer statsAnalyzer = new StatsAnalyzer(patternQty, 600000, 30, 100000, 1);
            foreach (var kvp in observedCounts)
            {
                ulong seq = kvp.Key;
                ulong count = kvp.Value;

                if (!countToSequences.ContainsKey(count))
                {
                    countToSequences[count] = new HashSet<ulong>();
                }
                countToSequences[count].Add(seq);
            }

            List<ulong> sortedCounts = countToSequences.Keys.OrderByDescending(c => c).ToList();
            Dictionary<ulong, HashSet<ulong>> topNExpected = new Dictionary<ulong, HashSet<ulong>>();

            int remainingItems = patternQty;
            ulong minCount = 0;

            foreach (var count in sortedCounts)
            {
                if (remainingItems < 0) break;
                topNExpected[count] = countToSequences[count];
                remainingItems -= countToSequences[count].Count;
                minCount = count;

            }

            List<ulong> sortedTopNCounts = topNExpected.Keys.ToList();
            sortedTopNCounts.Sort();

            statsAnalyzer.Add(instructionBuffer);

            Assert.That(statsAnalyzer.topNQueue.Count == patternQty, $"Exxpected {patternQty} in topNQueue found {statsAnalyzer.topNQueue.Count}");

            while (statsAnalyzer.topNQueue.TryDequeue(out ulong ngram, out ulong itemCount))
            {
                Assert.That(sortedTopNCounts[0] == itemCount, $"Expected count at level {sortedTopNCounts[0]} found {itemCount}");
                topNExpected[itemCount].Remove(ngram);
                if (topNExpected[itemCount].Count == 0)
                {
                    topNExpected.Remove(itemCount);
                    sortedTopNCounts.RemoveAt(0);
                }
            }

            Assert.That(topNExpected.Count <= 1, $"Expected at most one count level remaining in topN count map found {topNExpected.Count}");

            if (topNExpected.Count == 1)
                Assert.That(topNExpected.ContainsKey(minCount), $"Highest Count items were not found in queue");

        }

        public static (List<Instruction>, Dictionary<ulong, ulong>) RandNGrams(int qty, bool resets, int maxRepetitions)
        {

            List<Instruction> instructionBuffer = new List<Instruction>();
            Dictionary<ulong, ulong> parts = new Dictionary<ulong, ulong>();
            Action<ulong> countNGrams = (ulong _ngram) =>
                                         {
                                             if (parts.ContainsKey(_ngram))
                                             {
                                                 parts[_ngram] += 1;
                                             }
                                             else
                                             {
                                                 parts[_ngram] = 1;
                                             }
                                         };

            Random random = new Random();
            int randomNgramSize;
            for (int totalNgrams = 0; totalNgrams < qty; totalNgrams++)
            {
                randomNgramSize = random.Next(2, 7);
                Instruction[] ngram = new Instruction[randomNgramSize];
                for (int i = 0; i < randomNgramSize; i++)
                {
                    ngram[i] = RandInsruction();
                }

                var repetitions = random.Next(1, maxRepetitions);
                for (int j = 0; j < repetitions; j++)
                {
                    for (int i = 0; i < randomNgramSize; i++)
                    {
                        instructionBuffer.Add(ngram[i]);
                    }
                }
                if (resets) instructionBuffer.Add(NGrams.RESET);
            }

            NGrams ngrams = new NGrams();
            ngrams = NGrams.ProcessInstructions(instructionBuffer, ngrams, countNGrams);
            return (instructionBuffer, parts);
        }



        static Instruction RandInsruction()
        {
            Random random = new Random();
            byte randomByte = 0;
            string str = "1";
            while (char.IsDigit(str[0]) || str == "STOP" || str == "INVALID" || str == "NUMBER")
            {
                randomByte = (byte)random.Next(0, 256);
                str = ((Instruction)randomByte).ToString();
            }
            return (Instruction)randomByte;
        }


        private static IEnumerable<TestCaseData> NgramTestCases = NGramsTests.NgramTestCases;
        [Test, TestCaseSource(nameof(NgramTestCases))]
        public void validate_ngram_generation_exhaustive(Instruction[] transaction, (Instruction[] ngram, int count)[] ngrams)
        {
            Dictionary<ulong, ulong> counts = new Dictionary<ulong, ulong>();
            StatsAnalyzer statsAnalyzer = new StatsAnalyzer(100, 600000, 20, 100000, 1);
            foreach (Instruction instruction in transaction)
            {
                statsAnalyzer.Add(instruction);
            }
            Assert.That(statsAnalyzer.topNQueue.Count == ngrams.Count(), $" Total ngrams expected {ngrams.Count()}, found {statsAnalyzer.topNQueue.Count}");
            foreach ((Instruction[] ngram, int expectedCount) _ngram in ngrams)
            {
                counts[new NGrams(_ngram.ngram).ulong0] = (ulong)_ngram.expectedCount;
            }

            while (statsAnalyzer.topNQueue.TryDequeue(out ulong ngram, out ulong count))
            {
                Assert.That(counts.ContainsKey(ngram),
                        $"{new NGrams(ngram).ToString()} not present in testCase ");
                if (counts[ngram] >= count) counts[ngram] -= count;
                else counts[ngram] = 0; // we have over estimated
                if (counts[ngram] == 0)
                    counts.Remove(ngram);
            }

            if (counts.Count != 0)
            {
                foreach (KeyValuePair<ulong, ulong> kvp in counts)
                {
                    Console.WriteLine($"{new NGrams(kvp.Value).ToString()} was not found in TopN");
                }
            }

            Assert.That(counts.Count == 0,
                    $"{counts.Count} ngrams were missing in the TopN Queue");

        }

    }
}

