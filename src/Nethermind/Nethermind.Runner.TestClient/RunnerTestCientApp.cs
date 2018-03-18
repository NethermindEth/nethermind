/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;

namespace Nethermind.Runner.TestClient
{
    public class RunnerTestCientApp : IRunnerTestCientApp
    {
        private readonly IRunnerTestCient _cient;

        public RunnerTestCientApp(IRunnerTestCient cient)
        {
            _cient = cient;
        }

        public void Start()
        {
            while (true)
            {
                Console.WriteLine("Options: 1 - eth_protocolVersion, 2 - eth_getBlockByNumber, 3 - eth_accounts, e - exit");
                Console.WriteLine("Enter command: ");
                var action = Console.ReadLine();
                if (action.CompareIgnoreCaseTrim("e"))
                {
                    return;
                }
                else if (action.CompareIgnoreCaseTrim("1"))
                {
                    var result = Task.Run(() => _cient.SendEthProtocolVersion());
                    result.Wait();
                    PrintResult(result.Result);
                }
                else if (action.CompareIgnoreCaseTrim("2"))
                {
                    Console.Write("Block Nr: ");
                    var number = int.Parse(Console.ReadLine());
                    var result = Task.Run(() => _cient.SendEthGetBlockNumber($"0x{number}", false));
                    result.Wait();
                    PrintResult(result.Result);
                }
                else if (action.CompareIgnoreCaseTrim("3"))
                {
                    var result = Task.Run(() => _cient.SendEthAccounts());
                    result.Wait();
                    PrintResult(result.Result);
                }
                else
                {
                    Console.WriteLine("Incorrect command");
                }
            }
        }

        private void PrintResult(string result)
        {
            Console.WriteLine("Response:");
            Console.WriteLine(result);
        }
    }
}