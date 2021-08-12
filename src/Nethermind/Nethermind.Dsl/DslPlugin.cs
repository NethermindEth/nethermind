using System.Threading.Tasks;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain.Processing;
using Nethermind.Dsl.ANTLR;
using Nethermind.TxPool;
using Nethermind.Pipeline;
using Nethermind.Dsl.Pipeline;
using Nethermind.Core;
using System;
using Nethermind.Pipeline.Publishers;
using Nethermind.Int256;
using System.IO;
using Nethermind.Logging;
using System.Linq;

namespace Nethermind.Dsl
{
    public class DslPlugin : INethermindPlugin
    {
        public string Name { get; }

        public string Description { get; }

        public string Author { get; }

        private ParseTreeListener _listener;
        private INethermindApi _api;
        private ITxPool _txPool;
        private IBlockProcessor _blockProcessor;
        private IPipeline _pipeline;
        private IPipelineBuilder<Block, Block> _blockProcessorPipelineBuilder;
        private bool blockSource;
        private ILogger _logger; 
        private IDslConfig _config;

        public async Task Init(INethermindApi nethermindApi)
        {
            _api = nethermindApi;
            _txPool = _api.TxPool;
            _blockProcessor = _api.MainBlockProcessor;

            _config = _api.Config<IDslConfig>();
            if (_config.Enabled)
            {
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
                _listener.OnEnterInit = OnInitEntry;
                _listener.OnEnterExpression = OnExpressionEntry;
                _listener.OnEnterCondition = OnConditionEntry;
                _listener.OnExitInit = BuildPipeline;
                ParseTreeWalker.Default.Walk(_listener, tree);

                if (_logger.IsInfo) _logger.Info("DSL plugin initialized.");
            }
        }

        public Task InitNetworkProtocol()
        {
            _txPool = _api.TxPool;
            _blockProcessor = _api.MainBlockProcessor;
            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void OnInitEntry(AntlrTokenType tokenType, string tokenValue)
        {
            if (tokenType == AntlrTokenType.SOURCE)
            {
                if (tokenValue.Equals("BlockProcessor", StringComparison.InvariantCultureIgnoreCase))
                {
                    var sourceElement = new BlockProcessorSource<Block>(_blockProcessor);
                    _blockProcessorPipelineBuilder = new PipelineBuilder<Block, Block>(sourceElement);

                    return;
                }

                throw new ArgumentException($"Given token {tokenType} value {tokenValue} is not supported.");
            }
        }

        private void OnExpressionEntry(AntlrTokenType tokenType, string tokenValue)
        {
            switch (tokenType)
            {
                case AntlrTokenType.SOURCE:
                    break;
                case AntlrTokenType.WHERE: 
                    break;
                case AntlrTokenType.WATCH:
                    SetWatchOnPipeline(tokenValue);
                    break;
                case AntlrTokenType.PUBLISH:
                    AddPublisher(tokenValue);
                    break;
                default: throw new ArgumentException($"Given token is not supported {tokenType}");
            }
        }

        private void OnConditionEntry(string key, string symbol, string value)
        {
            if (blockSource)
            {
                switch (key)
                {
                    case "==":
                        _blockProcessorPipelineBuilder.AddElement(
                            new PipelineElement<Block, Block>(
                                condition: (b => b.GetType().GetProperty(key).GetValue(b).ToString() == value),
                                transformData: (b => b)
                            )
                        );
                        return;
                    case "!=":
                        _blockProcessorPipelineBuilder.AddElement(
                            new PipelineElement<Block, Block>(
                                condition: (b => b.GetType().GetProperty(key).GetValue(b).ToString() != value),
                                transformData: (b => b)
                            )
                        );
                        return;
                    case ">":
                        _blockProcessorPipelineBuilder.AddElement(
                            new PipelineElement<Block, Block>(
                                condition: (b => (UInt256)b.GetType().GetProperty(key).GetValue(b) > UInt256.Parse(value)),
                                transformData: (b => b)
                            )
                        );
                        return;
                    case "<":
                        _blockProcessorPipelineBuilder.AddElement(
                            new PipelineElement<Block, Block>(
                                condition: (b => (UInt256)b.GetType().GetProperty(key).GetValue(b) < UInt256.Parse(value)),
                                transformData: (b => b)
                            )
                        );
                        return;
                    case ">=":
                        _blockProcessorPipelineBuilder.AddElement(
                            new PipelineElement<Block, Block>(
                                condition: (b => (UInt256)b.GetType().GetProperty(key).GetValue(b) >= UInt256.Parse(value)),
                                transformData: (b => b)
                            )
                        );
                        return;
                    case "<=":
                        _blockProcessorPipelineBuilder.AddElement(
                            new PipelineElement<Block, Block>(
                                condition: (b => (UInt256)b.GetType().GetProperty(key).GetValue(b) <= UInt256.Parse(value)),
                                transformData: (b => b)
                            )
                        );
                        return;
                }
            }
        }

            private void SetWatchOnPipeline(string value)
            {
                value = value.ToLowerInvariant();
                switch (value)
                {
                    case "blocks":
                        _blockProcessorPipelineBuilder.AddElement(new PipelineElement<Block, Block>((block => true), (b => b)));
                        blockSource = true;
                        break;
                    case "transactions":
                        _blockProcessorPipelineBuilder.AddElement(new PipelineElement<Block, Transaction[]>(
                            (b => true),
                            (block => block.Transactions)
                        ));
                        blockSource = false;
                        break;
                }
            }

            private void AddPublisher(string publisherType)
            {
                if (publisherType.Equals("WebSockets", StringComparison.InvariantCultureIgnoreCase))
                {
                    if (_blockProcessorPipelineBuilder != null)
                    {
                        _blockProcessorPipelineBuilder.AddElement(new WebSocketsPublisher<Block, Block>("dsl", _api.EthereumJsonSerializer, _api.LogManager));
                    }
                }

                if (publisherType.Equals("LogPublisher", StringComparison.InvariantCultureIgnoreCase))
                {
                    if (_blockProcessorPipelineBuilder != null)
                    {
                        _blockProcessorPipelineBuilder.AddElement(new LogPublisher<Block, Block>(_api.EthereumJsonSerializer, _api.LogManager));
                    }
                }
            }

            private void BuildPipeline()
            {
                _pipeline = _blockProcessorPipelineBuilder.Build();
            }

            private async Task<string> LoadDSLScript()
            {
                var dirPath = Path.Combine(PathUtils.ExecutingDirectory, "DSL");
                if(_logger.IsInfo) _logger.Info($"Loading dsl script from {dirPath}");

                if(Directory.Exists(dirPath))
                {
                    var file = Directory.GetFiles("DSL", "*.txt").First(); 

                    return await File.ReadAllTextAsync(file);
                }

                throw new FileLoadException($"Could not find DSL directory at {dirPath} or the directory is empty");
            }
        }
    }
