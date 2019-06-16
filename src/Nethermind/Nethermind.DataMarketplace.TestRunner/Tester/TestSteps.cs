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
using System.IO;
using Microsoft.Extensions.Logging;
using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.TestRunner.Framework;
using Nethermind.DataMarketplace.TestRunner.JsonRpc;
using Nethermind.DataMarketplace.TestRunner.Tester.Steps;
using Nethermind.Dirichlet.Numerics;
using Org.BouncyCastle.Math;

namespace Nethermind.DataMarketplace.TestRunner.Tester
{
    public class TestBuilder
    {
        private readonly IProcessBuilder _processBuilder;
        private readonly ILogger<TestBuilder> _logger;
        internal List<TestStepBase> _steps;

        private static string _runnerDir;
        private static string _dbsDir;
        private static string _configsDir;

        static TestBuilder()
        {
            string testContextDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "context");
            _runnerDir = Path.Combine(testContextDir, "runner");
            _configsDir = Path.Combine(testContextDir, "configs");
            _dbsDir = Path.Combine(testContextDir, "dbs");

            if (Directory.Exists(testContextDir))
            {
                Directory.Delete(testContextDir, true);
            }

            Directory.CreateDirectory(_dbsDir);
            Directory.CreateDirectory(_configsDir);
        }

        public TestBuilder(IProcessBuilder processBuilder, ILogger<TestBuilder> logger)
        {
            _processBuilder = processBuilder;
            _logger = logger;
            
            if (!Directory.Exists(_runnerDir))
            {
                Directory.CreateDirectory(_runnerDir);
                CopyRunnerFiles(_runnerDir);
            }
        }

        public T SetContext<T>(T newContext) where T : ITestContext
        {
            newContext.SetBuilder(this);
            return newContext;
        }

        public IEnumerable<TestStepBase> Build()
        {
            return _steps;
        }

        private const int _startHttpPort = 8600;
        private const int _startPort = 30200;

        private byte _nodeCounter;

        public NethermindProcessWrapper CurrentNode { get; private set; }

        public TestBuilder SwitchNode(string node)
        {
            CurrentNode = _processes[node];
            return this;
        }
        
        public TestBuilder Wait(int delay = 5000, string name = "Wait")
        {
            _steps.Add(new WaitTestStep(name, delay));
            return this;
        }
        
        public TestBuilder StartCliqueNode(string name)
        {
            string baseConfigFile = "configs/baseCliqueConfig.cfg";
            CurrentNode = GetOrCreateNode(name, baseConfigFile);
            _steps.Add(new StartProcessTestStep($"Start clique node {name}", CurrentNode));
            return this;
        }
        
        public TestBuilder StartNode(string name, string baseConfigFile)
        {
            CurrentNode = GetOrCreateNode(name, baseConfigFile);
            _steps.Add(new StartProcessTestStep($"Start data provider node {name}", CurrentNode));
            return this;
        }

        private NethermindProcessWrapper GetOrCreateNode(string name, string baseConfigFile)
        {
            if (!_processes.ContainsKey(name))
            {
                string bootnodes = string.Empty;
                foreach ((_, NethermindProcessWrapper process) in _processes)
                {
                    bootnodes += $",{process.Enode}";
                }

                bootnodes = bootnodes.TrimStart(',');
                
                byte[] key = new byte[32];
                key[0] = 1;
                key[31] = _nodeCounter;
                string nodeKey = key.ToHexString();

                string dbDir = Path.Combine(_dbsDir, name);
                string configPath = Path.Combine(_configsDir, $"{name}.cfg");
                File.Copy(baseConfigFile, configPath);
                int p2pPort = _startPort + _nodeCounter;
                int httpPort = _startHttpPort + _nodeCounter;
                _logger.LogInformation($"Creating {name} at {p2pPort}, http://localhost:{httpPort}");
                _processes[name] = _processBuilder.Create(name, _runnerDir, configPath, dbDir, httpPort, p2pPort, nodeKey, bootnodes);
                _nodeCounter++;
            }

            return _processes[name];
        }

        private Dictionary<string, NethermindProcessWrapper> _processes = new Dictionary<string, NethermindProcessWrapper>();

        public TestBuilder Kill()
        {
            return Kill(CurrentNode.Name);
        }
        
        public TestBuilder Kill(string name)
        {
            _steps.Add(new KillProcessTestStep($"Kill {name}", _processes[name]));
            return this;
        }
        
        public TestBuilder Debug()
        {
            _steps.Add(new KillProcessTestStep($"Kill {CurrentNode}", _processes[CurrentNode.Name]));
            return this;
        }

        private void CopyRunnerFiles(string targetDirectory)
        {
            string sourceDirectory = Path.Combine(Directory.GetCurrentDirectory(), "../Nethermind.Runner/bin/Debug/netcoreapp2.1/");
            if (!Directory.Exists(sourceDirectory))
            {
                throw new IOException($"Runner not found at {sourceDirectory}");
            }

            _logger.LogInformation($"Copying runner files from {sourceDirectory} to {targetDirectory}");
            CopyDir(sourceDirectory, targetDirectory);
            string chainsDir = Path.Combine(Directory.GetCurrentDirectory(), "../chainspec");
            CopyDir(chainsDir, Path.Combine(targetDirectory, "chainspec"));
        }

        private void CopyDir(string sourceDirectory, string targetDirectory)
        {
            foreach (string file in Directory.GetFiles(sourceDirectory))
            {
                File.Copy(file, Path.Combine(targetDirectory, Path.GetFileName(file)), true);
            }

            foreach (string directory in Directory.GetDirectories(sourceDirectory))
            {
                string targetSubDir = Path.Combine(targetDirectory, Path.GetFileName(directory));
                Directory.CreateDirectory(targetSubDir);
                CopyDir(directory, targetSubDir);
            }
        }

        public TestBuilder NewScenario()
        {
            _steps = new List<TestStepBase>();
            return this;
        }
    }
}