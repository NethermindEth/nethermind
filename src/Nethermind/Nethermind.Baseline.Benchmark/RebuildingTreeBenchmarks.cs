using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Baseline.Tree;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Baseline.Benchmark
{
    public class RebuildingTreeBenchmarks
    {
        private ILogFinder _logFinder;
        private BaselineTreeHelper _baselineTreeHelper;

        [SetUp]
        public void Setup()
        {
            _logFinder = Substitute.For<ILogFinder>();
            _logFinder.FindLogs(Arg.Any<LogFilter>()).Returns(new List<FilterLog>() { new FilterLog(0,0,0,TestItem.KeccakA,0,TestItem.KeccakA,TestItem.AddressA,TestItem.KeccakA.Bytes, null)});
            _baselineTreeHelper = new BaselineTreeHelper();
        }

        [Test]
        public void Rebuild()
        {
            _baselineTreeHelper.RebuildEntireTree(TestItem.AddressA, Keccak.Zero, _logFinder);
        }
    }
}
