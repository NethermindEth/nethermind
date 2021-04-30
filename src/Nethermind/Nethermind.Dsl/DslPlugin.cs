using System.Threading.Tasks;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Dsl.ANTLR;
using System.IO;
using Nethermind.Logging;
using System.Linq;
using System.IO.Abstractions;

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
        private Interpreter? _interpreter;
        private ILogger? _logger;

        public async Task Init(INethermindApi nethermindApi)
        {
            _api = nethermindApi;

            _logger = _api.LogManager.GetClassLogger();
            if (_logger.IsInfo) _logger.Info("Initializing DSL plugin ...");

            var dslScript = await LoadDSLScript();

            var inputStream = new AntlrInputStream(dslScript);
            var lexer = new DslGrammarLexer(inputStream);
            var tokens = new CommonTokenStream(lexer);
            var parser = new DslGrammarParser(tokens);
            parser.BuildParseTree = true;
            IParseTree tree = parser.init();

            _listener = new ParseTreeListener();
            _interpreter = new Interpreter(_api, tree, _listener);
            ParseTreeWalker.Default.Walk(_listener, tree);

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

        private async Task<string> LoadDSLScript()
        {
            if (FileSystem == null)
            {
                FileSystem = new FileSystem();
            }

            var dirPath = FileSystem.Path.Combine(PathUtils.ExecutingDirectory, "DSL");
            if (_logger.IsInfo) _logger.Info($"Loading dsl script from {dirPath}");

            if (FileSystem.Directory.Exists(dirPath))
            {
                var file = FileSystem.Directory.GetFiles("DSL", "*.txt").First();

                return await FileSystem.File.ReadAllTextAsync(file);
            }

            throw new FileLoadException($"Could not find DSL directory at {dirPath} or the directory is empty");
        }
    }
}