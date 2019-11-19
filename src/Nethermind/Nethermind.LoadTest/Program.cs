//  Copyright (c) 2018 Demerzel Solutions Limited
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

using NBomber.CSharp;

namespace Nethermind.LoadTest
{
    internal static class Program
    {
        static void Main(string[] args)
        {
            var scenarios = new JsonRpcScenarios();
            NBomberRunner.RegisterScenarios(
                    scenarios.eth_blockNumber,
                    scenarios.eth_getBalance,
                    scenarios.eth_getBlockByNumber)
                .RunInConsole();
        }
    }
}