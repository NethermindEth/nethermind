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
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Json;
using Nethermind.Core.Logging;
using Nethermind.Runner;
using Nethermind.Runner.Config;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Nethermind.RunnerSpawner
{
    [JsonObject]
    public class SpawnerConfig
    {
        public string ReleasePath { get; set; }
        public string DbBasePath { get; set; }
        public Dictionary<string, JToken> Runners { get; set; }
    }

    internal class Program
    {
        private static ILogger _logger;

        public static int Main(string[] args)
        {
            _logger = new SimpleConsoleLogger();

            if (args.Length > 1)
            {
                _logger.Error("Expecting at most one argument - the name of the config file (default when empty).");
                Console.ReadLine();
                return 1;
            }

            string configFileName = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, args.Length == 1 ? args[0] : "spawner_discovery_large.json");
            string jsonText = File.ReadAllText(configFileName);

            IJsonSerializer serializer = new UnforgivingJsonSerializer();
            SpawnerConfig spawnerConfig = serializer.Deserialize<SpawnerConfig>(jsonText);

            var configTempDir = Path.Combine(Path.GetTempPath(), "SpawnerConfigs");
            if (!Directory.Exists(configTempDir))
            {
                Directory.CreateDirectory(configTempDir);
            }

            var files = Directory.GetFiles(configTempDir);
            foreach (var file in files)
            {
                File.Delete(file);
            }
            
            _logger.Info($"Storing all runner configs in: {configTempDir}");

            List<ProcessWrapper> wrappers = new List<ProcessWrapper>();
            foreach ((string name, JToken parameters) in spawnerConfig.Runners)
            {
                string serialized = serializer.Serialize(parameters, true);
                string singleConfigPath = Path.Combine(configTempDir, $"nethermind.runner.{name}.cfg");
                File.WriteAllText(singleConfigPath, serialized);

                try
                {
                    wrappers.Add(CreateAppConsole(spawnerConfig.ReleasePath, spawnerConfig.DbBasePath, name, new[] {singleConfigPath}));
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
            }

            ChaosMonkey chaosMonkey = new ChaosMonkey(_logger, new ChaosMonkey.ChaosMonkeyOptions {IntervalSeconds = 0, AllDownIntervalSeconds = 0}, wrappers.ToArray());
            chaosMonkey.Start();

            _logger.Info("Press ENTER to close all the spawned processes.");
            Console.ReadLine();

            chaosMonkey.Stop();

            _logger.Info("Press ENTER to exit.");

            Console.ReadKey();
            return 0;
        }

        private static ProcessWrapper CreateAppConsole(string path, string baseDbPath, string name, string[] args)
        {
            Process process = new Process();
            process.EnableRaisingEvents = false; // change to true if needed
            process.ErrorDataReceived += ProcessOnErrorDataReceived;
            process.OutputDataReceived += ProcessOnOutputDataReceived;
            process.Exited += ProcessOnExited;
            process.StartInfo.WorkingDirectory = path;
            process.StartInfo.FileName = "dotnet";
            var arguments = $"Nethermind.Runner.dll --config {string.Join(' ', args)}";
            if (!string.IsNullOrEmpty(baseDbPath))
            {
                arguments = $"{arguments} --baseDbPath {baseDbPath}";
            }
            process.StartInfo.Arguments = arguments;
            process.StartInfo.UseShellExecute = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.WindowStyle = ProcessWindowStyle.Normal;
            
            _logger.Info($"Starting {process.StartInfo.WorkingDirectory} {process.StartInfo.FileName} {process.StartInfo.Arguments}");
            if (!Directory.Exists(path))
            {
                _logger.Info($"Cannot find directory {path}");
            }

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