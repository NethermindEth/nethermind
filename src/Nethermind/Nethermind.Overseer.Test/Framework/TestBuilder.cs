using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;
using Nethermind.Overseer.Test.Framework.Steps;
using NUnit.Framework;

namespace Nethermind.Overseer.Test.Framework
{
    /// <summary>
    /// https://stackoverflow.com/questions/32112418/how-to-make-a-fluent-async-inferface-in-c-sharp
    /// </summary>
    public class TestBuilder
    {
        [TearDown]
        public void TearDown()
        {
            var passedCount = _results.Count(r => r.Passed);
            var failedCount = _results.Count - passedCount;

            TestContext.WriteLine("=========================== NDM TESTS RESULTS ===========================");
            TestContext.WriteLine($"NDM TESTS PASSED: {passedCount}, FAILED: {failedCount}");
            foreach (var testResult in _results)
            {
                string message = $"{testResult.Order}. {testResult.Name} has " +
                                 $"{(testResult.Passed ? "passed [+]" : "failed [-]")}";
                if (testResult.Passed)
                {
                    TestContext.WriteLine(message);
                }
                else
                {
                    TestContext.WriteLine(message);
                }
            }
        }
        
        /// <summary>
        /// Gets the task representing the fluent work.
        /// </summary>
        /// <value>
        /// The task.
        /// </value>
        public Task ScenarioCompletion { get; private set; }

        /// <summary>
        /// Queues up asynchronous work.
        /// </summary>
        /// <param name="work">The work to be queued.</param>
        public void QueueWork(Action work)
        {
            // queue up the work
            ScenarioCompletion = ScenarioCompletion.ContinueWith(task =>
            {
                work();
                return this;
            }, TaskContinuationOptions.OnlyOnRanToCompletion);
        }
        
        public void QueueWork(TestStepBase step)
        {
            // queue up the work
            ScenarioCompletion = ScenarioCompletion.ContinueWith(async task =>
            {
                TestContext.WriteLine($"Awaiting step {step.Name}");
                _results.Add(await step.ExecuteAsync());
                TestContext.WriteLine($"Step {step.Name} complete");
            }, TaskContinuationOptions.OnlyOnRanToCompletion).Unwrap();
        }

        private readonly ProcessBuilder _processBuilder;

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

        public TestBuilder()
        {
            _processBuilder = new ProcessBuilder();

            if (!Directory.Exists(_runnerDir))
            {
                Directory.CreateDirectory(_runnerDir);
                CopyRunnerFiles(_runnerDir);
            }

            // The entry point for the async work.
            // Spin up a completed task to start with 
            // so that we dont have to do null checks    
            this.ScenarioCompletion = Task.FromResult<int>(0);
        }

        public T SetContext<T>(T newContext) where T : ITestContext
        {
            newContext.SetBuilder(this);
            return newContext;
        }

        private const int _startHttpPort = 8600;
        private const int _startPort = 30200;

        private byte _nodeCounter;

        public NethermindProcessWrapper CurrentNode { get; private set; }

        public List<TestResult> _results = new List<TestResult>();
        
        public TestBuilder SwitchNode(string node)
        {
            CurrentNode = Nodes[node];
            return this;
        }

        public TestBuilder Wait(int delay = 5000, string name = "Wait")
        {
            QueueWork(() => Thread.Sleep(delay));
            return this;
        }

        public TestBuilder StartCliqueNode(string name)
        {
            return StartNode(name, "configs/cliqueNode.cfg");
        }
        
        public TestBuilder StartCliqueMiner(string name)
        {
            return StartNode(name, "configs/cliqueMiner.cfg");
        }
        
        public TestBuilder StartGoerliNode(string name)
        {
            return StartNode(name, "configs/goerliNode.cfg");
        }
        
        public TestBuilder StartGoerliMiner(string name)
        {
            return StartNode(name, "configs/goerliMiner.cfg");
        }

        public TestBuilder StartNode(string name, string baseConfigFile)
        {
            CurrentNode = GetOrCreateNode(name, baseConfigFile);
            var step = new StartProcessTestStep($"Start {name}", CurrentNode);
            QueueWork(step);
            return this;
        }

        private NethermindProcessWrapper GetOrCreateNode(string name, string baseConfigFile)
        {
            if (!Nodes.ContainsKey(name))
            {
                string bootnodes = string.Empty;
                foreach ((_, NethermindProcessWrapper process) in Nodes)
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
                TestContext.WriteLine($"Creating {name} at {p2pPort}, http://localhost:{httpPort}");
                Nodes[name] = _processBuilder.Create(name, _runnerDir, configPath, dbDir, httpPort, p2pPort, nodeKey, bootnodes);
                _nodeCounter++;
            }

            return Nodes[name];
        }

        public Dictionary<string, NethermindProcessWrapper> Nodes { get; } = new Dictionary<string, NethermindProcessWrapper>();

        public TestBuilder Kill()
        {
            return Kill(CurrentNode.Name);
        }

        public TestBuilder Kill(string name)
        {
            var step = new KillProcessTestStep($"Kill {name}", Nodes[name]);
            QueueWork(step);
            return this;
        }
        
        public TestBuilder KillAll()
        {
            foreach (KeyValuePair<string,NethermindProcessWrapper> keyValuePair in Nodes)
            {
                var step = new KillProcessTestStep($"Kill {keyValuePair.Key}", Nodes[keyValuePair.Key]);
                QueueWork(step);
            }
            
            return this;
        }

        private void CopyRunnerFiles(string targetDirectory)
        {
            string sourceDirectory = Path.Combine(Directory.GetCurrentDirectory(), "../../../../Nethermind.Runner/bin/Debug/netcoreapp3.0/");
            if (!Directory.Exists(sourceDirectory))
            {
                throw new IOException($"Runner not found at {sourceDirectory}");
            }

            TestContext.WriteLine($"Copying runner files from {sourceDirectory} to {targetDirectory}");
            CopyDir(sourceDirectory, targetDirectory);
            string chainsDir = Path.Combine(Directory.GetCurrentDirectory(), "chainspec");
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
    }
}