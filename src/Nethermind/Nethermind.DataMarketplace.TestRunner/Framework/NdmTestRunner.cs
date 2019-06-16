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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nethermind.DataMarketplace.TestRunner.Tester;

namespace Nethermind.DataMarketplace.TestRunner.Framework
{
    public class NdmTestRunner : BackgroundService
    {
        private readonly INdmTester _ndmTester;
        private readonly ILogger<NdmTestRunner> _logger;

        public NdmTestRunner(INdmTester ndmTester, ILogger<NdmTestRunner> logger)
        {
            _ndmTester = ndmTester;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var testResults = await _ndmTester.TestAsync();
            var passedCount = testResults.Results.Count(r => r.Passed);
            var failedCount = testResults.Results.Count - passedCount;

            _logger.LogInformation("=========================== NDM TESTS RESULTS ===========================");
            _logger.LogInformation($"NDM TESTS PASSED: {passedCount}, FAILED: {failedCount}");
            foreach (var testResult in testResults.Results)
            {
                string message = $"{testResult.Order}. {testResult.Name} has " +
                                 $"{(testResult.Passed ? "passed [+]" : "failed [-]")}";
                if (testResult.Passed)
                {
                    _logger.LogInformation(message);
                }
                else
                {
                    _logger.LogError(message);
                }
            }

            Console.ReadLine();
            await Task.CompletedTask;
            Environment.Exit(0);
        }
    }
}