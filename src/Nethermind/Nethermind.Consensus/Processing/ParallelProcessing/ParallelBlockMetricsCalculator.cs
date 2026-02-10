// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Consensus.Processing.ParallelProcessing;

internal static class ParallelBlockMetricsCalculator
{
    public static ParallelBlockMetrics Calculate(Transaction[] transactions, MultiVersionMemory multiVersionMemory)
    {
        int txCount = transactions.Length;
        if (txCount == 0)
        {
            return ParallelBlockMetrics.Empty;
        }

        bool[] hasValidationDependency = HasValidationDependency();
        bool[] hasReadDependency = new bool[txCount];
        bool[] hasWriteDependency = new bool[txCount];
        HashSet<ParallelStateKey> previousWrites = new();
        List<ParallelStateKey> writeKeys = new();

        for (int txIndex = 0; txIndex < txCount; txIndex++)
        {
            bool readDependency = false;
            multiVersionMemory.ForEachReadSet(txIndex, read =>
            {
                if (!readDependency && !read.Version.IsEmpty)
                {
                    readDependency = true;
                }
            });
            hasReadDependency[txIndex] = readDependency;

            bool writeDependency = false;
            writeKeys.Clear();
            multiVersionMemory.ForEachWriteSet(txIndex, (key, _) =>
            {
                if (!writeDependency && previousWrites.Contains(key))
                {
                    writeDependency = true;
                }

                writeKeys.Add(key);
            });
            hasWriteDependency[txIndex] = writeDependency;

            foreach (ParallelStateKey key in writeKeys)
            {
                previousWrites.Add(key);
            }
        }

        long reexecutions = 0;
        long revalidations = 0;
        long blockedReads = 0;
        for (int txIndex = 0; txIndex < txCount; txIndex++)
        {
            if (hasValidationDependency[txIndex])
            {
                revalidations++;
            }

            if (hasReadDependency[txIndex])
            {
                blockedReads++;
            }

            if (hasValidationDependency[txIndex] || hasReadDependency[txIndex] || hasWriteDependency[txIndex])
            {
                reexecutions++;
            }
        }

        long parallelizationPercent = Metrics.CalculateParallelizationPercent(txCount, reexecutions);
        return new ParallelBlockMetrics(txCount, reexecutions, revalidations, blockedReads, parallelizationPercent);

        bool[] HasValidationDependency()
        {
            bool[] result = new bool[txCount];
            for (int txIndex = 1; txIndex < txCount; txIndex++)
            {
                Address? sender = transactions[txIndex].SenderAddress;
                if (sender is not null)
                {
                    for (int i = txIndex - 1; i >= 0; i--)
                    {
                        Transaction prevTx = transactions[i];
                        if (prevTx.SenderAddress == sender)
                        {
                            result[txIndex] = true;
                            break;
                        }

                        if (prevTx.HasAuthorizationList)
                        {
                            foreach (AuthorizationTuple tuple in prevTx.AuthorizationList)
                            {
                                if (tuple.Authority == sender)
                                {
                                    result[txIndex] = true;
                                    break;
                                }
                            }

                            if (result[txIndex])
                            {
                                break;
                            }
                        }
                    }
                }
            }

            return result;
        }
    }
}
