using System;
using System.Collections.Generic;
using System.Linq;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Dsl.Pipeline;
using Nethermind.Int256;
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

        public Interpreter(INethermindApi api, string script)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));

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
            if(value.Equals("BlockProcessor", StringComparison.InvariantCultureIgnoreCase))
            {
                var sourceElement = new BlockProcessorSource<Block>(_api.MainBlockProcessor);
                _blockPipelineBuilder = new PipelineBuilder<Block, Block>(sourceElement);

                _blockSource = true;

                return;
            }
            else if(value.Equals("TxPool", StringComparison.InvariantCultureIgnoreCase))
            {
                var sourceElement = new TxPoolSource<Transaction>(_api.TxPool);
                _transactionPipelineBuilder = new PipelineBuilder<Transaction, Transaction>(sourceElement);

                _blockSource = false;

                return;
            }
        }

        private void AddWatch(string value)
        {
            switch (value.ToLowerInvariant())
            {
                case "blocks":
                    _blockPipelineBuilder = _blockPipelineBuilder.AddElement(
                        new PipelineElement<Block, Block>(
                            condition: (block => true),
                            transformData: (b => b)
                            )
                    );
                    break;
                case "transactions":
                // with watch on transactions we need to change for transactions pipeline, hence new source
                    var sourceElement = new TxPoolSource<Transaction>(_api.TxPool);
                    _transactionPipelineBuilder = new PipelineBuilder<Transaction, Transaction>(sourceElement);

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
            return operation switch
            {
                "==" => new PipelineElement<Transaction, Transaction>(
                            condition: (t => t.GetType().GetProperty(key).GetValue(t).ToString() == value),
                            transformData: (t => t)),
                "!=" => new PipelineElement<Transaction, Transaction>(
                            condition: (t => t.GetType().GetProperty(key).GetValue(t).ToString() != value),
                            transformData: (t => t)),
                ">" => new PipelineElement<Transaction, Transaction>(
                            condition: (t => (UInt256)t.GetType().GetProperty(key).GetValue(t) > UInt256.Parse(value)),
                            transformData: (t => t)),
                "<" => new PipelineElement<Transaction, Transaction>(
                            condition: (t => (UInt256)t.GetType().GetProperty(key).GetValue(t) < UInt256.Parse(value)),
                            transformData: (t => t)),
                ">=" => new PipelineElement<Transaction, Transaction>(
                            condition: (t => (UInt256)t.GetType().GetProperty(key).GetValue(t) >= UInt256.Parse(value)),
                            transformData: (t => t)),
                "<=" => new PipelineElement<Transaction, Transaction>(
                            condition: (t => (UInt256)t.GetType().GetProperty(key).GetValue(t) <= UInt256.Parse(value)),
                            transformData: (t => t)),
                _ => null
            };
        }

        private PipelineElement<Block, Block> GetNextBlockElement(string key, string operation, string value)
        {
            return operation switch
            {
                "==" => new PipelineElement<Block, Block>(
                            condition: (b => b.GetType().GetProperty(key).GetValue(b).ToString() == value),
                            transformData: (b => b)),
                "!=" => new PipelineElement<Block, Block>(
                            condition: (b => b.GetType().GetProperty(key).GetValue(b).ToString() != value),
                            transformData: (b => b)),
                ">" => new PipelineElement<Block, Block>(
                            condition: (b => (UInt256)b.GetType().GetProperty(key).GetValue(b) > UInt256.Parse(value)),
                            transformData: (b => b)),
                "<" => new PipelineElement<Block, Block>(
                            condition: (b => (UInt256)b.GetType().GetProperty(key).GetValue(b) < UInt256.Parse(value)),
                            transformData: (b => b)),
                ">=" => new PipelineElement<Block, Block>(
                            condition: (b => (UInt256)b.GetType().GetProperty(key).GetValue(b) >= UInt256.Parse(value)),
                            transformData: (b => b)),
                "<=" => new PipelineElement<Block, Block>(
                            condition: (b => (UInt256)b.GetType().GetProperty(key).GetValue(b) <= UInt256.Parse(value)),
                            transformData: (b => b)),
                _ => null
            };
        }

        private void AddPublisher(string publisher)
        {
            if(_blockSource)
            {
                AddBlockPublisher(publisher);
                return;
            }

            AddTransactionPublisher(publisher);

            BuildPipeline();
        }

        private void AddBlockPublisher(string publisher)
        {
            if (publisher.Equals("WebSockets", StringComparison.InvariantCultureIgnoreCase))
            {
                if (_blockPipelineBuilder != null)
                {
                    _blockPipelineBuilder =_blockPipelineBuilder.AddElement(new WebSocketsPublisher<Block, Block>("dsl", _api.EthereumJsonSerializer));
                }
            }

            BuildPipeline();
        }

        private void AddTransactionPublisher(string publisher)
        {
            if (publisher.Equals("WebSockets", StringComparison.InvariantCultureIgnoreCase))
            {
                if (_blockPipelineBuilder != null)
                {
                    _transactionPipelineBuilder = _transactionPipelineBuilder.AddElement(new WebSocketsPublisher<Transaction, Transaction>("dsl", _api.EthereumJsonSerializer));
                }
            }
        }

        private void BuildPipeline()
        {
            if(_blockSource)
            {
                Pipeline = _blockPipelineBuilder.Build();
            }
            else
            {
                Pipeline = _transactionPipelineBuilder.Build();
            }
        }
    }
}