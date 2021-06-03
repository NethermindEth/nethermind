using System;
using System.Collections.Generic;
using System.IO.Pipelines;
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
        private readonly ParseTreeWalker _parseTreeWalker;
        private PipelineSource _pipelineSource;
        private readonly ILogger _logger;

        // pipelines builders for each flow
        private IPipelineBuilder<Block, Block> _blocksPipelineBuilder;
        private IPipelineBuilder<Transaction, Transaction> _transactionsPipelineBuilder;
        private IPipelineBuilder<TxReceipt, TxReceipt> _eventsPipelineBuilder;
        private IPipelineBuilder<Transaction, Transaction> _pendingTransactionsPipelineBuilder;

        public Interpreter(INethermindApi api, string script)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _logger = api.LogManager.GetClassLogger();

            var inputStream = new AntlrInputStream(script);
            var lexer = new DslGrammarLexer(inputStream);
            var tokens = new CommonTokenStream(lexer);
            var parser = new DslGrammarParser(tokens)
            {
                BuildParseTree = true
            };

            _tree = parser.tree();

            _treeListener = new ParseTreeListener
            {
                OnWatchExpression = AddWatch,
                OnCondition = AddCondition,
                OnAndCondition = AddAndCondition,
                OnOrCondition = AddOrCondition,
                OnPublishExpression = AddPublisher
            };


            _parseTreeWalker = new ParseTreeWalker();
            _parseTreeWalker.Walk(_treeListener, _tree);
        }

        private void AddWatch(string value)
        {
            switch (value.ToLowerInvariant())
            {
                case "blocks":
                    var blocksSource = new BlocksSource<Block>(_api.MainBlockProcessor, _logger);
                    _blocksPipelineBuilder = new PipelineBuilder<Block, Block>(blocksSource);
                    _pipelineSource = PipelineSource.Blocks;

                    break;
                case "events":
                    var eventSource = new EventsSource<TxReceipt>(_api.MainBlockProcessor);
                    _eventsPipelineBuilder = new PipelineBuilder<TxReceipt, TxReceipt>(eventSource);
                    _pipelineSource = PipelineSource.Events;

                    break;
                case "transactions":
                    var processedTransactionsSource = new ProcessedTransactionsSource<Transaction>(_api.MainBlockProcessor);
                    _transactionsPipelineBuilder = new PipelineBuilder<Transaction, Transaction>(processedTransactionsSource);
                    _pipelineSource = PipelineSource.Transactions;

                    break;
                case "newpending":
                    var pendingTransactionsSource = new PendingTransactionsSource<Transaction>(_api.TxPool);
                    _pendingTransactionsPipelineBuilder = new PipelineBuilder<Transaction, Transaction>(pendingTransactionsSource);
                    _pipelineSource = PipelineSource.PendingTransactions;

                    break;
            }
        }

        private void AddCondition(string key, string symbol, string value)
        {
            switch (_pipelineSource)
            {
                case PipelineSource.Blocks:
                    PipelineElement<Block, Block> blockElement = GetNextBlockElement(key, symbol, value);
                    _blocksPipelineBuilder = _blocksPipelineBuilder.AddElement(blockElement);
                    break;
                case PipelineSource.Transactions:
                    PipelineElement<Transaction, Transaction> txElement = GetNextTransactionElement(key, symbol, value);
                    _transactionsPipelineBuilder = _transactionsPipelineBuilder.AddElement(txElement);
                    break;
                case PipelineSource.PendingTransactions:
                    PipelineElement<Transaction, Transaction> pendingTxElement = GetNextTransactionElement(key, symbol, value);
                    _pendingTransactionsPipelineBuilder = _pendingTransactionsPipelineBuilder.AddElement(pendingTxElement);
                    break;
                case PipelineSource.Events:
                    PipelineElement<TxReceipt, TxReceipt> eventElement = GetNextEventElement(key, symbol, value);
                    _eventsPipelineBuilder = _eventsPipelineBuilder.AddElement(eventElement);
                    break;
            }
        }

        private void AddAndCondition(string key, string symbol, string value)
        {
            AddCondition(key, symbol, value); // AND condition is just adding another element to the pipeline
        }

        private void AddOrCondition(string key, string symbol, string value)
        {
            // OR operation add conditions to the last element in the pipeline
            switch (_pipelineSource)
            {
                case PipelineSource.Blocks:
                    var blockElement = GetNextBlockElement(key, symbol, value);
                    var blockCondition = blockElement.Conditions.Last();
                    var lastBlockElement = (PipelineElement<Block, Block>) _blocksPipelineBuilder.LastElement;
                    lastBlockElement.AddCondition(blockCondition);
                    break;
                case PipelineSource.Transactions:
                    var txElement = GetNextTransactionElement(key, symbol, value);
                    var txCondition = txElement.Conditions.Last();
                    var lastTxElement = (PipelineElement<Transaction, Transaction>) _transactionsPipelineBuilder.LastElement;
                    lastTxElement.AddCondition(txCondition);
                    break;
                case PipelineSource.PendingTransactions:
                    var pendingTxElement = GetNextTransactionElement(key, symbol, value);
                    var pendingTxCondition = pendingTxElement.Conditions.Last();
                    var lastPendingTxElement = (PipelineElement<Transaction, Transaction>) _pendingTransactionsPipelineBuilder.LastElement;
                    lastPendingTxElement.AddCondition(pendingTxCondition);
                    break;
                case PipelineSource.Events:
                    var eventElement = GetNextEventElement(key, symbol, value);
                    var eventElementCondition = eventElement.Conditions.Last();
                    var lastEventElement = (PipelineElement<TxReceipt, TxReceipt>) _eventsPipelineBuilder.LastElement;
                    lastEventElement.AddCondition(eventElementCondition);
                    break;
            }
        }

        private PipelineElement<Transaction, Transaction> GetNextTransactionElement(string key, string operation, string value)
        {
            _logger.Info($"Adding new pipeline element with OPERATION: {operation}");
            return operation switch
            {
                "IS" => new PipelineElement<Transaction, Transaction>(
                    condition: (t => t.GetType().GetProperty(key).GetValue(t).ToString() == value),
                    transformData: (t => t)),
                "==" => new PipelineElement<Transaction, Transaction>(
                    condition: (t => t.GetType().GetProperty(key).GetValue(t).ToString() == value),
                    transformData: (t => t)),
                "!=" => new PipelineElement<Transaction, Transaction>(
                    condition: (t => t.GetType().GetProperty(key)?.GetValue(t)?.ToString() != value),
                    transformData: (t => t)),
                ">" => new PipelineElement<Transaction, Transaction>(
                    condition: (t => (UInt256) t.GetType().GetProperty(key)?.GetValue(t) > UInt256.Parse(value)),
                    transformData: (t => t)),
                "<" => new PipelineElement<Transaction, Transaction>(
                    condition: (t => (UInt256) t.GetType().GetProperty(key)?.GetValue(t) < UInt256.Parse(value)),
                    transformData: (t => t)),
                ">=" => new PipelineElement<Transaction, Transaction>(
                    condition: (t => (UInt256) t.GetType().GetProperty(key)?.GetValue(t) >= UInt256.Parse(value)),
                    transformData: (t => t)),
                "<=" => new PipelineElement<Transaction, Transaction>(
                    condition: (t => (UInt256) t.GetType().GetProperty(key)?.GetValue(t) <= UInt256.Parse(value)),
                    transformData: (t => t)),
                "CONTAINS" => new PipelineElement<Transaction, Transaction>(
                    condition: (t => (t.GetType().GetProperty(key)?.GetValue(t) as byte[]).ToHexString().Contains(value)),
                    transformData: (t => t)),
                _ => null
            };
        }

        private PipelineElement<Block, Block> GetNextBlockElement(string key, string operation, string value)
        {
            return operation switch
            {
                "==" => new PipelineElement<Block, Block>(
                    condition: (b => b.GetType().GetProperty(key)?.GetValue(b)?.ToString() == value),
                    transformData: (b => b)),
                "!=" => new PipelineElement<Block, Block>(
                    condition: (b => b.GetType().GetProperty(key)?.GetValue(b)?.ToString() != value),
                    transformData: (b => b)),
                ">" => new PipelineElement<Block, Block>(
                    condition: (b => (UInt256) b.GetType().GetProperty(key)?.GetValue(b) > UInt256.Parse(value)),
                    transformData: (b => b)),
                "<" => new PipelineElement<Block, Block>(
                    condition: (b => (UInt256) b.GetType().GetProperty(key)?.GetValue(b) < UInt256.Parse(value)),
                    transformData: (b => b)),
                ">=" => new PipelineElement<Block, Block>(
                    condition: (b => (UInt256) b.GetType().GetProperty(key)?.GetValue(b) >= UInt256.Parse(value)),
                    transformData: (b => b)),
                "<=" => new PipelineElement<Block, Block>(
                    condition: (b => (UInt256) b.GetType().GetProperty(key)?.GetValue(b) <= UInt256.Parse(value)),
                    transformData: (b => b)),
                _ => null
            };
        }

        private PipelineElement<TxReceipt, TxReceipt> GetNextEventElement(string key, string operation, string value)
        {
            return operation switch
            {
                "IS" => new PipelineElement<TxReceipt, TxReceipt>(
                    condition: (t => t.GetType().GetProperty(key)?.GetValue(t)?.ToString() == value),
                    transformData: (t => t)),
                "==" => new PipelineElement<TxReceipt, TxReceipt>(
                    condition: (t => t.GetType().GetProperty(key)?.GetValue(t)?.ToString() == value),
                    transformData: (t => t)),
                "IS NOT" => new PipelineElement<TxReceipt, TxReceipt>(
                    condition: (t => t.GetType().GetProperty(key)?.GetValue(t)?.ToString() != value),
                    transformData: (t => t)),
                "!=" => new PipelineElement<TxReceipt, TxReceipt>(
                    condition: (t => t.GetType().GetProperty(key)?.GetValue(t)?.ToString() != value),
                    transformData: (t => t)),
                _ => null
            };
        }

        private void AddPublisher(string publisher, string path)
        {
            switch (_pipelineSource)
            {
                case PipelineSource.Blocks:
                    AddBlocksPublisher(publisher, path);
                    break;
                case PipelineSource.Transactions:
                    AddTransactionsPublisher(publisher, path);
                    break;
                case PipelineSource.PendingTransactions:
                    AddPendingTransactionsPublisher(publisher, path);
                    break;
                case PipelineSource.Events:
                    AddEventsPublisher(publisher, path);
                    break;
            }

            BuildPipeline();
        }

        private void AddBlocksPublisher(string publisher, string path)
        {
            if (publisher.Equals("WebSockets", StringComparison.InvariantCultureIgnoreCase))
            {
                if (_blocksPipelineBuilder != null)
                {
                    if (_logger.IsInfo) _logger.Info($"Adding block publisher with path: {path}");
                    var webSocketsPublisher = new WebSocketsPublisher<Block, Block>(path, _api.EthereumJsonSerializer, _logger);
                    _api.WebSocketsManager.AddModule(webSocketsPublisher);
                    _blocksPipelineBuilder = _blocksPipelineBuilder.AddElement(webSocketsPublisher);
                }
            }

            BuildPipeline();
        }

        private void AddTransactionsPublisher(string publisher, string path)
        {
            if (publisher.Equals("WebSockets", StringComparison.InvariantCultureIgnoreCase))
            {
                if (_transactionsPipelineBuilder == null) return;
                var webSocketsPublisher = new WebSocketsPublisher<Transaction, Transaction>(path, _api.EthereumJsonSerializer, _logger);
                _api.WebSocketsManager.AddModule(webSocketsPublisher);
                _transactionsPipelineBuilder = _transactionsPipelineBuilder.AddElement(webSocketsPublisher);
            }
        }
        
        private void AddPendingTransactionsPublisher(string publisher, string path)
        {
            if (publisher.Equals("WebSockets", StringComparison.InvariantCultureIgnoreCase))
            {
                if (_pendingTransactionsPipelineBuilder == null) return;
                var webSocketsPublisher = new WebSocketsPublisher<Transaction, Transaction>(path, _api.EthereumJsonSerializer, _logger);
                _api.WebSocketsManager.AddModule(webSocketsPublisher);
                _pendingTransactionsPipelineBuilder = _pendingTransactionsPipelineBuilder.AddElement(webSocketsPublisher);
            }
        }
        
        private void AddEventsPublisher(string publisher, string path)
        {
            if (publisher.Equals("WebSockets", StringComparison.InvariantCultureIgnoreCase))
            {
                if (_eventsPipelineBuilder == null) return;
                var webSocketsPublisher = new WebSocketsPublisher<TxReceipt, TxReceipt>(path, _api.EthereumJsonSerializer, _logger);
                _api.WebSocketsManager.AddModule(webSocketsPublisher);
                _eventsPipelineBuilder = _eventsPipelineBuilder.AddElement(webSocketsPublisher);

                return;
            }
        }
        
        private void BuildPipeline()
        {
            Pipeline = _pipelineSource switch
            {
                PipelineSource.Blocks => _blocksPipelineBuilder.Build(),
                PipelineSource.Transactions => _transactionsPipelineBuilder.Build(),
                PipelineSource.PendingTransactions => _pendingTransactionsPipelineBuilder.Build(),
                PipelineSource.Events => _eventsPipelineBuilder.Build(),
                _ => Pipeline
            };
        }
    }
}