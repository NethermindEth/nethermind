// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core.Test;

namespace Ethereum.Test.Base
{
    public class TestsSourceLoader(ITestLoadStrategy testLoadStrategy, string path, string? wildcard = null)
        : ITestSourceLoader
    {
        private readonly ITestLoadStrategy _testLoadStrategy = testLoadStrategy ?? throw new ArgumentNullException(nameof(testLoadStrategy));
        private readonly string _path = path ?? throw new ArgumentNullException(nameof(path));

        public IEnumerable<EthereumTest> LoadTests() =>
            TestChunkFilter.FilterByChunk(_testLoadStrategy.Load(_path, wildcard));

        public IEnumerable<TTestType> LoadTests<TTestType>() where TTestType : EthereumTest =>
            TestChunkFilter.FilterByChunk(OfTestType<TTestType>(_testLoadStrategy.Load(_path, wildcard)));

        /// <summary>
        /// Keeps the tests of <typeparamref name="TTestType"/> and restates each file that failed to load as one of them.
        /// </summary>
        /// <remarks>
        /// <see cref="FileTestsSource"/> reports an unreadable file as a <see cref="FailedToLoadTest"/>, which derives
        /// from none of the concrete test types, so a plain type filter would drop the file without a failing case.
        /// </remarks>
        /// <exception cref="InvalidDataException">A file failed to load and <typeparamref name="TTestType"/> has no placeholder for it.</exception>
        private static IEnumerable<TTestType> OfTestType<TTestType>(IEnumerable<EthereumTest> tests) where TTestType : EthereumTest
        {
            foreach (EthereumTest test in tests)
            {
                if (test is TTestType typed)
                {
                    yield return typed;
                }
                else if (test is FailedToLoadTest failed)
                {
                    yield return AsLoadFailure<TTestType>(failed);
                }
            }
        }

        private static TTestType AsLoadFailure<TTestType>(FailedToLoadTest failed) where TTestType : EthereumTest
        {
            EthereumTest placeholder = typeof(TTestType) switch
            {
                Type t when t == typeof(GeneralStateTest) => new GeneralStateTest(),
                Type t when t == typeof(BlockchainTest) => new BlockchainTest(),
                Type t when t == typeof(TransactionTest) => new TransactionTest(),
                _ => throw new InvalidDataException(
                    $"{failed.Name} failed to load and cannot be reported as {typeof(TTestType).Name}: {failed.LoadFailure}")
            };

            placeholder.Name = failed.Name;
            placeholder.Category = failed.Category;
            placeholder.ChainId = failed.ChainId;
            placeholder.LoadFailure = failed.LoadFailure;
            return (TTestType)placeholder;
        }
    }
}
