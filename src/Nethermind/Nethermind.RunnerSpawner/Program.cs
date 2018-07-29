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
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Logging;
using Nethermind.Runner;
using Newtonsoft.Json;

namespace Nethermind.RunnerSpawner
{
    [JsonObject]
    public class SpawnerConfig
    {
        public string ReleasePath { get; set; }
        public Dictionary<string, InitParams> Runners { get; set; }
    }

    internal class Program
    {
        private static ILogger _logger;

        public static int Main(string[] args)
        {
            _logger = new NLogLogger("spawner.logs.txt");

            if (args.Length > 1)
            {
                _logger.Error("Expecting at most one argument - the name of the config file (default when empty).");
                Console.ReadLine();
                return 1;
            }

            string configFileName = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, args.Length == 1 ? args[0] : "spawner.json");
            string jsonText = File.ReadAllText(configFileName);

            IJsonSerializer serializer = new UnforgivingJsonSerializer();
            SpawnerConfig spawnerConfig = serializer.Deserialize<SpawnerConfig>(jsonText);

            List<ProcessWrapper> wrappers = new List<ProcessWrapper>();
            foreach ((string name, InitParams parameters) in spawnerConfig.Runners)
            {
                string serialized = serializer.Serialize(parameters, true);
                string singleConfigPath = Path.Combine(Path.GetTempPath(), $"nethermind.runner.{name}.config.json");
                File.WriteAllText(singleConfigPath, serialized);

                try
                {
                    wrappers.Add(CreateAppConsole(spawnerConfig.ReleasePath, name, new[] {singleConfigPath}));
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
            }

            ChaosMonkey chaosMonkey = new ChaosMonkey(_logger, new ChaosMonkey.ChaosMonkeyOptions {IntervalSeconds = 10, AllDownIntervalSeconds = 90}, wrappers.ToArray());
            chaosMonkey.Start();

            _logger.Info("Press ENTER to close all the spawned processes.");
            Console.ReadLine();

            chaosMonkey.Stop();

            _logger.Info("Press ENTER to exit.");

            Console.ReadKey();
            return 0;
        }

        private static ProcessWrapper CreateAppConsole(string path, string name, string[] args)
        {
            Process process = new Process();
            process.EnableRaisingEvents = false; // change to true if needed
            process.ErrorDataReceived += ProcessOnErrorDataReceived;
            process.OutputDataReceived += ProcessOnOutputDataReceived;
            process.Exited += ProcessOnExited;
            process.StartInfo.WorkingDirectory = path;
            process.StartInfo.FileName = "dotnet";
            process.StartInfo.Arguments = $"Nethermind.Runner.dll --config {string.Join(' ', args)}";
            process.StartInfo.UseShellExecute = true;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.WindowStyle = ProcessWindowStyle.Normal;
            _logger.Info($"Starting {process.StartInfo.WorkingDirectory} {process.StartInfo.FileName} {process.StartInfo.Arguments}");

            return new ProcessWrapper(name, process);
        }

        private static void ProcessOnExited(object sender, EventArgs eventArgs)
        {
        }

        private static void ProcessOnOutputDataReceived(object sender, DataReceivedEventArgs dataReceivedEventArgs)
        {
        }

        private static void ProcessOnErrorDataReceived(object sender, DataReceivedEventArgs dataReceivedEventArgs)
        {
        }
    }
}