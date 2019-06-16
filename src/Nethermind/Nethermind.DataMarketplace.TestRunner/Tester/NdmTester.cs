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
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nethermind.DataMarketplace.TestRunner.Tester.Scenarios;

namespace Nethermind.DataMarketplace.TestRunner.Tester
{
    public class NdmTester : INdmTester
    {
        private readonly CliqueMinersScenario _cliqueMiners;
        private readonly LaunchNodeScenario _launchNodeScenario;
        private readonly DefaultTestScenario _defaultTestScenario;
        private readonly ILogger<INdmTester> _logger;

        public NdmTester(CliqueMinersScenario cliqueMinersScenario, LaunchNodeScenario launchNodeScenario, DefaultTestScenario defaultTestScenario, ILogger<INdmTester> logger)
        {
            _cliqueMiners = cliqueMinersScenario;
            _launchNodeScenario = launchNodeScenario;
            _defaultTestScenario = defaultTestScenario;
            _logger = logger;
        }

        public async Task<TestResults> TestAsync()
        {
//            ITestScenario[] scenarios = {_defaultTestScenario, _launchNodeScenario, _cliqueMiners};
            ITestScenario[] scenarios = {_defaultTestScenario};

            _logger.LogInformation("=========================== STARTING NDM TESTS ===========================");

            List<TestResult> results = new List<TestResult>();
            foreach (ITestScenario testScenario in scenarios)
            {
                try
                {
                    results.AddRange(await TestScenario(testScenario));
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    throw;
                }
            }

            _logger.LogInformation("=========================== FINISHED NDM TESTS ===========================");

            Console.ReadLine();

            return new TestResults
            {
                Results = results
            };
        }

        private async Task<List<TestResult>> TestScenario(ITestScenario testScenario)
        {
            _logger.LogInformation($"=========================== {testScenario.Name} ===========================");

            var results = new List<TestResult>();
            int order = 1;
            foreach (var step in testScenario.Steps)
            {
                try
                {
                    _logger.LogInformation($"Executing test step: " +
                                           $"'{step.Name}' ({order}/{testScenario.Steps.Count()}).");
                    var testResult = await step.ExecuteAsync();
                    await Task.Delay(1000);

                    Debug.Assert(order == testResult.Order);
                    _logger.LogInformation($"Test step: '{step.Name}' ({testResult.Order}/{testScenario.Steps.Count()}) has " +
                                           $"{(testResult.Passed ? "passed" : "failed")}.");
                    results.Add(testResult);
                }
                catch (Exception e)
                {
                    _logger.LogError($"Failed {e}");
                    throw;
                }

                order++;
            }

            return results;
        }
    }
}