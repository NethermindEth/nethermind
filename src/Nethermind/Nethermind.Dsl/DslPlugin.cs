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
using Nethermind.Blockchain.Find;

namespace Nethermind.Dsl
{
    public class DslPlugin : INethermindPlugin
    {
        public string Name { get; }

        public string Description { get; }

        public string Author { get; }

        private DslGrammarListener _listener;
        private INethermindApi _api;
        private ITxPool _txPool;
        private IBlockProcessor _blockProcessor;
        private IPipeline _pipeline;
        private IPipelineBuilder<Block, Block> _blockProcessorPipelineBuilder;

        public Task Init(INethermindApi nethermindApi)
        {
            _api = nethermindApi;
            _txPool = _api.TxPool;
            _blockProcessor = _api.MainBlockProcessor;

            var inputStream = new AntlrInputStream("SOURCE BlockProcessor WATCH Transactions WHERE To = 0x9cea2ed9e47059260c97d697f82b8a14efa61ea5 PUBLISH WebSockets");
            var lexer = new DslGrammarLexer(inputStream);
            var tokens = new CommonTokenStream(lexer);
            var parser = new DslGrammarParser(tokens);
            parser.BuildParseTree = true;
            IParseTree tree = parser.init();

            _listener = new DslGrammarListener();
            _listener.OnEnterInit = OnInitEntry;
            _listener.OnEnterExpression = OnExpressionEntry;
            ParseTreeWalker.Default.Walk(_listener, tree);

            return Task.CompletedTask;
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
                case AntlrTokenType.WATCH:
                    SetWatchOnPipeline(tokenValue);
                    break;
                case AntlrTokenType.WHERE:
                    
                    break;
                case AntlrTokenType.PUBLISH:
                    AddPublisher(tokenValue);
                    break;
                default: throw new ArgumentException($"Given token is not supported {tokenType}");
            }
        }

        private void SetWatchOnPipeline(string value)
        {
            value = value.ToLowerInvariant();
            switch (value)
            {
                case "blocks":
                    _blockProcessorPipelineBuilder.AddElement(new PipelineElement<Block, Block>(block => block));
                    break;
                case "transactions":
                    _blockProcessorPipelineBuilder.AddElement(new PipelineElement<Block, Transaction[]>(block => block.Transactions));
                    break;
            }
        }

        private void AddCondition(string condition)
        {

        }

        private void AddPublisher(string publisherType)
        {
            if (publisherType.Equals("WebSockets", StringComparison.InvariantCultureIgnoreCase))
            {
                if (_blockProcessorPipelineBuilder != null)
                {
                    _blockProcessorPipelineBuilder.AddElement(new WebSocketsPublisher<Block, Block>("dsl", _api.EthereumJsonSerializer));
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
    }
}