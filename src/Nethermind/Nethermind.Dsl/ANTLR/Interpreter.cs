using System;
using System.Linq;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using Microsoft.Extensions.Logging.Abstractions;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Dsl.Pipeline;
using Nethermind.Dsl.Pipeline.Builders;
using Nethermind.Dsl.Pipeline.Sources;
using Nethermind.Logging;
using Nethermind.Pipeline;
using Nethermind.Pipeline.Publishers;

namespace Nethermind.Dsl.ANTLR
{
    public class Interpreter
    {
        private readonly INethermindApi _api;
        private readonly IParseTree _tree;
        private readonly ParseTreeListener _treeListener;
        private readonly ParseTreeWalker _parseTreeWalker;
        private PipelineSource _pipelineSource;
        private readonly ILogger _logger;

        // pipelines builders for each flow
        private IPipelineBuilder<Block, Block> _blocksPipelineBuilder;
        private IPipelineBuilder<Transaction, Transaction> _transactionsPipelineBuilder;
        private IPipelineBuilder<LogEntry, LogEntry> _eventsPipelineBuilder;
        private IPipelineBuilder<Transaction, Transaction> _pendingTransactionsPipelineBuilder;

        //builders of elements in pipelines
        private readonly BlockElementsBuilder _blockElementsBuilder;
        private readonly TransactionElementsBuilder _transactionElementsBuilder;
        private readonly PendingTransactionElementsBuilder _pendingTransactionElementsBuilder;
        private readonly EventElementsBuilder _eventElementsBuilder;

        public string Script { get; }

        //pipelines for each flow
        public IPipeline BlocksPipeline { get; private set; }
        public IPipeline TransactionsPipeline { get; private set; }
        public IPipeline PendingTransactionsPipeline { get; private set; }
        public IPipeline EventsPipeline { get; private set; }

        public Interpreter(
            INethermindApi api,
            string script,
            BlockElementsBuilder blockElementsBuilder = null,
            TransactionElementsBuilder transactionElementsBuilder = null,
            PendingTransactionElementsBuilder pendingTransactionElementsBuilder = null,
            EventElementsBuilder eventElementsBuilder = null)
        {
            Script = script;
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _logger = api.LogManager.GetClassLogger();
            _blockElementsBuilder = blockElementsBuilder ?? new BlockElementsBuilder(_api.MainBlockProcessor);
            _transactionElementsBuilder = transactionElementsBuilder ?? new TransactionElementsBuilder(_api.MainBlockProcessor);
            _pendingTransactionElementsBuilder = pendingTransactionElementsBuilder ?? new PendingTransactionElementsBuilder(_api.TxPool);
            _eventElementsBuilder = eventElementsBuilder ?? new EventElementsBuilder(_api.MainBlockProcessor);


            var inputStream = new AntlrInputStream(Script);
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
                    var blocksSource = _blockElementsBuilder.GetSourceElement();
                    _blocksPipelineBuilder = new PipelineBuilder<Block, Block>(blocksSource);
                    _pipelineSource = PipelineSource.Blocks;

                    break;
                case "events":
                    var eventSource = _eventElementsBuilder.GetSourceElement();
                    _eventsPipelineBuilder = new PipelineBuilder<LogEntry, LogEntry>(eventSource);
                    _pipelineSource = PipelineSource.Events;

                    break;
                case "transactions":
                    var processedTransactionsSource = _transactionElementsBuilder.GetSourceElement();
                    _transactionsPipelineBuilder = new PipelineBuilder<Transaction, Transaction>(processedTransactionsSource);
                    _pipelineSource = PipelineSource.Transactions;

                    break;
                case "newpending":
                    var pendingTransactionsSource = _pendingTransactionElementsBuilder.GetSourceElement();
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
                    PipelineElement<Block, Block> blockElement = _blockElementsBuilder.GetConditionElement(key, symbol, value);
                    _blocksPipelineBuilder = _blocksPipelineBuilder.AddElement(blockElement);
                    break;
                case PipelineSource.Transactions:
                    PipelineElement<Transaction, Transaction> txElement = _transactionElementsBuilder.GetConditionElement(key, symbol, value);
                    _transactionsPipelineBuilder = _transactionsPipelineBuilder.AddElement(txElement);
                    break;
                case PipelineSource.PendingTransactions:
                    PipelineElement<Transaction, Transaction> pendingTxElement = _pendingTransactionElementsBuilder.GetConditionElement(key, symbol, value);
                    _pendingTransactionsPipelineBuilder = _pendingTransactionsPipelineBuilder.AddElement(pendingTxElement);
                    break;
                case PipelineSource.Events:
                    PipelineElement<LogEntry, LogEntry> eventElement = _eventElementsBuilder.GetConditionElement(key, symbol, value);
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
                    var blockElement = _blockElementsBuilder.GetConditionElement(key, symbol, value);
                    var blockCondition = blockElement.Conditions.Last();
                    var lastBlockElement = (PipelineElement<Block, Block>) _blocksPipelineBuilder.LastElement;
                    lastBlockElement.AddCondition(blockCondition);
                    break;
                case PipelineSource.Transactions:
                    var txElement = _transactionElementsBuilder.GetConditionElement(key, symbol, value);
                    var txCondition = txElement.Conditions.Last();
                    var lastTxElement = (PipelineElement<Transaction, Transaction>) _transactionsPipelineBuilder.LastElement;
                    lastTxElement.AddCondition(txCondition);
                    break;
                case PipelineSource.PendingTransactions:
                    var pendingTxElement = _pendingTransactionElementsBuilder.GetConditionElement(key, symbol, value);
                    var pendingTxCondition = pendingTxElement.Conditions.Last();
                    var lastPendingTxElement = (PipelineElement<Transaction, Transaction>) _pendingTransactionsPipelineBuilder.LastElement;
                    lastPendingTxElement.AddCondition(pendingTxCondition);
                    break;
                case PipelineSource.Events:
                    var eventElement = _eventElementsBuilder.GetConditionElement(key, symbol, value);
                    var eventElementCondition = eventElement.Conditions.Last();
                    var lastEventElement = (PipelineElement<LogEntry, LogEntry>) _eventsPipelineBuilder.LastElement;
                    lastEventElement.AddCondition(eventElementCondition);
                    break;
            }
        }

        private void AddPublisher(string publisher, string value)
        {
            publisher = publisher.ToLowerInvariant();

            switch (publisher)
            {
                case "websockets": AddWebSocketsPublisher(value);
                    break;
                case "telegram": AddTelegramPublisher(value);
                    break;
                case "discord": AddDiscordPublisher(value);
                    break;
            };
        }

        private void AddTelegramPublisher(string chatId)
        {
            var telegramPublisher = new TelegramPublisher(_api.EthereumJsonSerializer, chatId);

            AddBlocksPublisher(telegramPublisher);
            AddTransactionsPublisher(telegramPublisher);
            AddPendingTransactionsPublisher(telegramPublisher);
            AddEventsPublisher(telegramPublisher);

            BuildPipelines();
        }

        private void AddWebSocketsPublisher(string path)
        {
            var webSocketsPublisher = new WebSocketsPublisher(path, _api.EthereumJsonSerializer, _logger);
            _api.WebSocketsManager.AddModule(webSocketsPublisher);

            AddBlocksPublisher(webSocketsPublisher);
            AddTransactionsPublisher(webSocketsPublisher);
            AddPendingTransactionsPublisher(webSocketsPublisher);
            AddEventsPublisher(webSocketsPublisher);

            BuildPipelines();
        }

        private void AddDiscordPublisher(string chatId)
        {
            var discordPublisher = new DiscordPublisher(_api.EthereumJsonSerializer, chatId);

            AddBlocksPublisher(discordPublisher);
            AddTransactionsPublisher(discordPublisher);
            AddPendingTransactionsPublisher(discordPublisher);
            AddEventsPublisher(discordPublisher);

            BuildPipelines();
        }

        private void AddBlocksPublisher(IPublisher publisher)
        {
            _blocksPipelineBuilder = _blocksPipelineBuilder?.AddPublisher(publisher);
        }

        private void AddTransactionsPublisher(IPublisher publisher)
        {
            _transactionsPipelineBuilder = _transactionsPipelineBuilder?.AddPublisher(publisher);
        }

        private void AddPendingTransactionsPublisher(IPublisher publisher)
        {
            _pendingTransactionsPipelineBuilder = _pendingTransactionsPipelineBuilder?.AddPublisher(publisher);
        }

        private void AddEventsPublisher(IPublisher publisher)
        {
            _eventsPipelineBuilder = _eventsPipelineBuilder?.AddPublisher(publisher);
        }

        private void BuildPipelines()
        {
            BlocksPipeline = _blocksPipelineBuilder?.Build();
            TransactionsPipeline = _transactionsPipelineBuilder?.Build();
            PendingTransactionsPipeline = _pendingTransactionsPipelineBuilder?.Build();
            EventsPipeline = _eventsPipelineBuilder?.Build();
        }
    }
}
