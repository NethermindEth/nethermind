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

using System;
using System.Linq;
using System.Threading.Tasks;

namespace Nethermind.DataMarketplace.Consumers.Test.EndToEnd
{
    class Program
    {
        public static async Task Main(string[] args)
        {
            Console.WriteLine(Figgle.FiggleFonts.Doom.Render("NDM Consumer E2E"));
            var jsonRpcUrl = GetValue("JSON_RPC_URL", args?.FirstOrDefault(), "http://localhost:8545");
            var inputDisabled = GetValue("INPUT_DISABLED", args?.Skip(1).FirstOrDefault(), "false") is "true";
            var client = Environment.GetEnvironmentVariable("HOSTNAME") ?? "ndm";
            var pullDataDelay = GetDefaultDataOptions("PULL_DATA_DELAY");
            var pullDataRetries = GetDefaultDataOptions("PULL_DATA_RETRIES");
            var pullDataFailures = GetDefaultDataOptions("PULL_DATA_FAILURES", 100);
            
            Console.WriteLine($"JSON RPC URL: {jsonRpcUrl}, Client: {client}, Pull data delay: {pullDataDelay} ms, " +
                              $"Retries: {pullDataRetries}, Failures: {pullDataFailures}");

            if (!inputDisabled)
            {
                Console.WriteLine("Press any key to start the scenario.");
                Console.ReadKey();
            }

            try
            {
                var scenario = new Scenario(client, jsonRpcUrl, pullDataDelay, pullDataRetries, pullDataFailures);
                await scenario.RunAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
            
            if (!inputDisabled)
            {
                Console.WriteLine("Press any key to quit.");
                Console.ReadKey();
            }
        }

        private static int GetDefaultDataOptions(string env, int @default = 10)
        {
            if (!int.TryParse(env, out var value) || value <= 0)
            {
                value = @default;
            }

            return value;
        }

        private static string GetValue(string env, string arg, string @default)
        {
            var value = Environment.GetEnvironmentVariable(env);
            if (string.IsNullOrWhiteSpace(value))
            {
                value = arg;
            }

            return string.IsNullOrWhiteSpace(value) ? @default : value;
        }
    }
}