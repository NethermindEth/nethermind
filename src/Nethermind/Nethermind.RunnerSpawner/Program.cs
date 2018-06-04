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
using Microsoft.AspNetCore.Builder;
using Nethermind.Core;
using Nethermind.Runner;

namespace Nethermind.RunnerSpawner
{
    // TODO: because of the limitations of .NET Core we cannot have two dlls 'published' to a single folder
    // TODO: there was a fix in the .proj file but it was very hacky and high maintenance (to make it run both on Widnows or Linux)
    // TODO: need to redesign Spawner to just point at a folder with the runners
    internal class Program
    {
        private static readonly Dictionary<string, Process> Processes = new Dictionary<string, Process>();

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
            Dictionary<string, InitParams> runnerParameters = serializer.Deserialize<Dictionary<string, InitParams>>(jsonText);

            foreach ((string name, InitParams parameters) in runnerParameters)
            {
                string serialized = serializer.Serialize(parameters, true);
                string singleConfigPath = Path.Combine(Path.GetTempPath(), $"nethermind.runner.{name}.config.json");
                File.WriteAllText(singleConfigPath, serialized);

                CreateAppConsole(name, new[] {singleConfigPath});
            }

            _logger.Info("Press ENTER to close all the spawned processes.");
            Console.ReadLine();
            foreach ((string name, Process process) in Processes)
            {
                _logger.Info($"Closing {name}...");
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(5000);
                    if (process.HasExited)
                    {
                        _logger.Info($"{name} closed.");    
                    }
                    else
                    {
                        _logger.Error($"{name} could not be closed.");
                    }
                }
                else
                {
                    _logger.Info($"{name} already exited.");
                }
                
                process.Close();
            }

            _logger.Info("Press ENTER to exit.");

            Console.ReadKey();
            return 0;
        }

        private static void CreateAppConsole(string name, string[] args)
        {
            try
            {
                Process process = new Process();
                process.EnableRaisingEvents = false; // change to true if needed
                process.ErrorDataReceived += ProcessOnErrorDataReceived;
                process.OutputDataReceived += ProcessOnOutputDataReceived;
                process.Exited += ProcessOnExited;
                process.StartInfo.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                process.StartInfo.FileName = "dotnet";
                process.StartInfo.Arguments = $"Nethermind.Runner.dll --config {string.Join(' ', args)}";
                process.StartInfo.UseShellExecute = true;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.WindowStyle = ProcessWindowStyle.Normal;
                _logger.Info($"Starting {process.StartInfo.WorkingDirectory} {process.StartInfo.FileName} {process.StartInfo.Arguments}");
                process.Start();

                Processes.Add(name, process);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
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