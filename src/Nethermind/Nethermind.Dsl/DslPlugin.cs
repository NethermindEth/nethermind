using System.Threading.Tasks;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Dsl.ANTLR;
using System.IO;
using Nethermind.Logging;
using System.IO.Abstractions;
using System.Collections.Generic;

#nullable enable
namespace Nethermind.Dsl
{
    public class DslPlugin : INethermindPlugin
    {
        public string Name { get; } = "DslPlugin";
        public string Description { get; } = "Plugin created in order to let users create their own DSL scripts used in data extraction from chain";
        public string Author { get; } = "Nethermind team";
        public IFileSystem? FileSystem;

        private INethermindApi? _api;
        private ParseTreeListener? _listener;
        private List<Interpreter>? _interpreters;
        private ILogger? _logger;

        public async Task Init(INethermindApi nethermindApi)
        {
            _api = nethermindApi;

            _logger = _api.LogManager.GetClassLogger();
            if (_logger.IsInfo) _logger.Info("Initializing DSL plugin ...");

            IEnumerable<string> dslScripts = LoadDSLScript();
            _interpreters = new List<Interpreter>();

            foreach(var script in dslScripts)
            {
                _interpreters.Add(new Interpreter(_api, script));
            }

            if (_logger.IsInfo) _logger.Info("DSL plugin initialized.");
        }

        public Task InitNetworkProtocol()
        {
            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private IEnumerable<string> LoadDSLScript()
        {
            if (FileSystem == null)
            {
                FileSystem = new FileSystem();
            }

            var dirPath = FileSystem.Path.Combine(PathUtils.ExecutingDirectory, "DSL");
            if (_logger.IsInfo) _logger.Info($"Loading dsl scripts from {dirPath}");

            if (FileSystem.Directory.Exists(dirPath))
            {
                string[] files = FileSystem.Directory.GetFiles("DSL", "*.txt");

                foreach(var file in files)
                {
                    yield return FileSystem.File.ReadAllText(file);
                }
            }
            else
            {
                throw new FileLoadException($"Could not find DSL directory at {dirPath} or the directory is empty");
            }
        }
    }
}