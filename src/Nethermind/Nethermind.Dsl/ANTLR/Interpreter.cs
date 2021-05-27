using System;
using System.Collections.Generic;
using System.Linq;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Dsl.Pipeline;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pipeline;
using Nethermind.Pipeline.Publishers;

namespace Nethermind.Dsl.ANTLR
{
    public class Interpreter
    {
        public IPipeline Pipeline; 
        private readonly INethermindApi _api;
        private readonly IParseTree _tree;
        private readonly ParseTreeListener _treeListener;
        private IPipelineBuilder<Block, Block> _blockPipelineBuilder;
        private IPipelineBuilder<Transaction, Transaction> _transactionPipelineBuilder;
        private readonly ParseTreeWalker _parseTreeWalker;
        private bool _blockSource;
        private readonly ILogger _logger;

        public Interpreter(INethermindApi api, string script)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _logger = api.LogManager.GetClassLogger();

            var inputStream = new AntlrInputStream(script);
            var lexer = new DslGrammarLexer(inputStream);
            var tokens = new CommonTokenStream(lexer);
            var parser = new DslGrammarParser(tokens);
            parser.BuildParseTree = true;
            _tree = parser.tree();

            _treeListener = new ParseTreeListener();

            _treeListener.OnSourceExpression = AddSource;
            _treeListener.OnWatchExpression = AddWatch;
            _treeListener.OnCondition = AddCondition;
            _treeListener.OnAndCondition = AddAndCondition;
            _treeListener.OnOrCondition = AddOrCondition;
            _treeListener.OnPublishExpression = AddPublisher;

            _parseTreeWalker = new ParseTreeWalker();
            _parseTreeWalker.Walk(_treeListener, _tree);
        }

        private void AddSource(string value)
        {
        }

        private void AddWatch(string value)
        {
            switch (value.ToLowerInvariant())
            {
                case "blocks":
                    var blocksSource = new BlocksSource<Block>(_api.MainBlockProcessor, _logger);
                    _blockPipelineBuilder = new PipelineBuilder<Block, Block>(blocksSource);
                    _blockSource = true;

                    break;
                case "transactions":
                    var processedTransactionsSource = new ProcessedTransactionsSource<Transaction>(_api.MainBlockProcessor);
                    _transactionPipelineBuilder = new PipelineBuilder<Transaction, Transaction>(processedTransactionsSource);
                    _blockSource = false;

                    break;
                case "newpending":
                    // with watch on transactions we need to change for transactions pipeline, hence new source
                    var pendingTransactionsSource = new PendingTransactionsSource<Transaction>(_api.TxPool);
                    _transactionPipelineBuilder = new PipelineBuilder<Transaction, Transaction>(pendingTransactionsSource);

                    _blockSource = false;
                    break;
            }
        }

        private void AddCondition(string key, string symbol, string value)
        {
            if(_blockSource)
            {
                var blockElement = GetNextBlockElement(key, symbol, value);
                _blockPipelineBuilder = _blockPipelineBuilder.AddElement(blockElement);
                return;
            }

            var txElement = GetNextTransactionElement(key, symbol, value);
            _transactionPipelineBuilder = _transactionPipelineBuilder.AddElement(txElement);
        }

        private void AddAndCondition(string key, string symbol, string value)
        {
            AddCondition(key, symbol, value); // AND condition is just adding another element to the pipeline
        }

        private void AddOrCondition(string key, string symbol, string value)
        {
            // OR operation add conditions to the last element in the pipeline
            if(_blockSource)
            {
                var blockElement = (PipelineElement<Block, Block>)GetNextBlockElement(key, symbol, value);
                var blockCondition = blockElement.Conditions.Last();
                var lastBlockElement = (PipelineElement<Block, Block>)_blockPipelineBuilder.LastElement;
                lastBlockElement.AddCondition(blockCondition);
                return;
            }

            var txElement = (PipelineElement<Transaction, Transaction>)GetNextTransactionElement(key, symbol, value);
            var txCondition = txElement.Conditions.Last();
            var lastTxElement = (PipelineElement<Transaction, Transaction>)_transactionPipelineBuilder.LastElement;
            lastTxElement.AddCondition(txCondition);
        }

        private PipelineElement<Transaction, Transaction> GetNextTransactionElement(string key, string operation, string value)
        {
            _logger.Info($"Adding new pipeline element with OPERATION: {operation}");
            return operation switch
            {
                "IS" => new PipelineElement<Transaction, Transaction>(
                            condition: (t => t.GetType().GetProperty(key).GetValue(t).ToString() == value),
                            transformData: (t => t), _logger),
                "NOT" => new PipelineElement<Transaction, Transaction>(
                            condition: (t => t.GetType().GetProperty(key).GetValue(t).ToString() != value),
                            transformData: (t => t), _logger),
                ">" => new PipelineElement<Transaction, Transaction>(
                            condition: (t => (UInt256)t.GetType().GetProperty(key).GetValue(t) > UInt256.Parse(value)),
                            transformData: (t => t), _logger),
                "<" => new PipelineElement<Transaction, Transaction>(
                            condition: (t => (UInt256)t.GetType().GetProperty(key).GetValue(t) < UInt256.Parse(value)),
                            transformData: (t => t), _logger),
                ">=" => new PipelineElement<Transaction, Transaction>(
                            condition: (t => (UInt256)t.GetType().GetProperty(key).GetValue(t) >= UInt256.Parse(value)),
                            transformData: (t => t), _logger),
                "<=" => new PipelineElement<Transaction, Transaction>(
                            condition: (t => (UInt256)t.GetType().GetProperty(key).GetValue(t) <= UInt256.Parse(value)),
                            transformData: (t => t), _logger),
                "CONTAINS" => new PipelineElement<Transaction, Transaction>(
                            condition: (t => Bytes.ToHexString(t.GetType().GetProperty(key).GetValue(t) as byte[]).Contains(value)),
                            transformData: (t => t), _logger), 
                _ => null
            };
        }
        
        private PipelineElement<Block, Block> GetNextBlockElement(string key, string operation, string value)
        {
            return operation switch
            {
                "IS" => new PipelineElement<Block, Block>(
                            condition: (b => b.GetType().GetProperty(key).GetValue(b).ToString() == value),
                            transformData: (b => b), _logger),
                "NOT" => new PipelineElement<Block, Block>(
                            condition: (b => b.GetType().GetProperty(key).GetValue(b).ToString() != value),
                            transformData: (b => b), _logger),
                ">" => new PipelineElement<Block, Block>(
                            condition: (b => (UInt256)b.GetType().GetProperty(key).GetValue(b) > UInt256.Parse(value)),
                            transformData: (b => b), _logger),
                "<" => new PipelineElement<Block, Block>(
                            condition: (b => (UInt256)b.GetType().GetProperty(key).GetValue(b) < UInt256.Parse(value)),
                            transformData: (b => b), _logger),
                ">=" => new PipelineElement<Block, Block>(
                            condition: (b => (UInt256)b.GetType().GetProperty(key).GetValue(b) >= UInt256.Parse(value)),
                            transformData: (b => b), _logger),
                "<=" => new PipelineElement<Block, Block>(
                            condition: (b => (UInt256)b.GetType().GetProperty(key).GetValue(b) <= UInt256.Parse(value)),
                            transformData: (b => b), _logger),
                _ => null
            };
        }

        private void AddPublisher(string publisher, string path)
        {
            if(_blockSource)
            {
                AddBlockPublisher(publisher, path);
                return;
            }

            AddTransactionPublisher(publisher, path);

            BuildPipeline();
        }

        private void AddBlockPublisher(string publisher,string path)
        {
            if (publisher.Equals("WebSockets", StringComparison.InvariantCultureIgnoreCase))
            {
                if (_blockPipelineBuilder != null)
                {
                    if(_logger.IsInfo) _logger.Info($"Adding block publisher with path: {path}");
                    var webSocketsPublisher = new WebSocketsPublisher<Block, Block>(path, _api.EthereumJsonSerializer, _logger); 
                    _api.WebSocketsManager.AddModule(webSocketsPublisher);
                    _blockPipelineBuilder =_blockPipelineBuilder.AddElement(webSocketsPublisher);
                }
            }

            BuildPipeline();
        }

        private void AddTransactionPublisher(string publisher, string path)
        {
            if (publisher.Equals("WebSockets", StringComparison.InvariantCultureIgnoreCase))
            {
                if (_transactionPipelineBuilder != null)
                {
                    var webSocketsPublisher = new WebSocketsPublisher<Transaction, Transaction>(path, _api.EthereumJsonSerializer, _logger);
                    _api.WebSocketsManager.AddModule(webSocketsPublisher);
                    _transactionPipelineBuilder = _transactionPipelineBuilder.AddElement(webSocketsPublisher);

                    return; 
                }
            }
        }

        private void BuildPipeline()
        {
            if(_blockSource)
            {
                if(_logger.IsInfo) _logger.Info("Building pipeline...");
                Pipeline = _blockPipelineBuilder.Build();
            }
            else
            {
                Pipeline = _transactionPipelineBuilder.Build();
            }
        }
    }
}
