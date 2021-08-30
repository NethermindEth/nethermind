﻿//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using Ethereum.Test.Base;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace Nethermind.EthereumTests.Benchmark
{
    [SimpleJob(RunStrategy.ColdStart, warmupCount:1, targetCount: 5)]
    public class CallsWithLargeData : GeneralStateTestBase
    {
        [Benchmark]
        public void Gas10M_0xDead()
        {
            FileTestsSource source = new(@"AdditionalTests\CallsWithLargeData\10MGas_0xdead.json");
            var tests = source.LoadGeneralStateTests();
        
            foreach (GeneralStateTest test in tests)
            {
                RunTest(test);
            }
        }
        
        [Benchmark]
        public void Gas10M_BnAdd()
        {
            FileTestsSource source = new(@"AdditionalTests\CallsWithLargeData\10MGas_bnAdd.json");
            var tests = source.LoadGeneralStateTests();
        
            foreach (GeneralStateTest test in tests)
            {
                RunTest(test);
            }
        }
    }
}
