using System;
using System.Collections.Generic;
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
        public Stack<IPipeline> Pipelines = new Stack<IPipeline>();
        private readonly INethermindApi _api;
        private readonly IParseTree _tree;
        private readonly ParseTreeListener _treeListener;
        private IPipelineBuilder<Block, Block> _blockPipelineBuilder;
        private IPipelineBuilder<Transaction, Transaction> _transactionPipelineBuilder;
        private bool _blockSource;

        public Interpreter(INethermindApi api, IParseTree tree, ParseTreeListener treeListener)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _tree = tree ?? throw new ArgumentNullException(nameof(tree));
            _treeListener = treeListener ?? throw new ArgumentNullException(nameof(treeListener));

            _treeListener.OnEnterExpression = AddExpression; 
            _treeListener.OnEnterCondition = AddCondition;
            _treeListener.OnExit = BuildPipeline;
        }

        private void AddExpression(AntlrTokenType tokenType, string value)
        {
            switch (tokenType)
            {
                case AntlrTokenType.SOURCE: 
                    AddSource(value);
                break;
                case AntlrTokenType.WATCH:
                    AddWatch(value);
                break;
                case AntlrTokenType.PUBLISH:
                    AddPublisher(value);
                break;
            }
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
            }
        }

        private void AddCondition(string key, string symbol, string value)
        {
            if (_blockSource)
            {
                switch (symbol)
                {
                    case "==":
                        _blockPipelineBuilder =_blockPipelineBuilder.AddElement(
                            new PipelineElement<Block, Block>(
                                condition: (b => b.GetType().GetProperty(key).GetValue(b).ToString() == value),
                                transformData: (b => b)
                            )
                        );
                        return;
                    case "!=":
                        _blockPipelineBuilder =_blockPipelineBuilder.AddElement(
                            new PipelineElement<Block, Block>(
                                condition: (b => b.GetType().GetProperty(key).GetValue(b).ToString() != value),
                                transformData: (b => b)
                            )
                        );
                        return;
                    case ">":
                        _blockPipelineBuilder =_blockPipelineBuilder.AddElement(
                            new PipelineElement<Block, Block>(
                                condition: (b => (UInt256)b.GetType().GetProperty(key).GetValue(b) > UInt256.Parse(value)),
                                transformData: (b => b)
                            )
                        );
                        return;
                    case "<":
                        _blockPipelineBuilder =_blockPipelineBuilder.AddElement(
                            new PipelineElement<Block, Block>(
                                condition: (b => (UInt256)b.GetType().GetProperty(key).GetValue(b) < UInt256.Parse(value)),
                                transformData: (b => b)
                            )
                        );
                        return;
                    case ">=":
                        _blockPipelineBuilder =_blockPipelineBuilder.AddElement(
                            new PipelineElement<Block, Block>(
                                condition: (b => (UInt256)b.GetType().GetProperty(key).GetValue(b) >= UInt256.Parse(value)),
                                transformData: (b => b)
                            )
                        );
                        return;
                    case "<=":
                        _blockPipelineBuilder =_blockPipelineBuilder.AddElement(
                            new PipelineElement<Block, Block>(
                                condition: (b => (UInt256)b.GetType().GetProperty(key).GetValue(b) <= UInt256.Parse(value)),
                                transformData: (b => b)
                            )
                        );
                        return;
                }
            }

        }

        private void AddPublisher(string publisher)
        {
            if (publisher.Equals("WebSockets", StringComparison.InvariantCultureIgnoreCase))
            {
                if (_blockPipelineBuilder != null)
                {
                    _blockPipelineBuilder =_blockPipelineBuilder.AddElement(new WebSocketsPublisher<Block, Block>("dsl", _api.EthereumJsonSerializer));
                }
            }
        }

        private void BuildPipeline()
        {
            if(_blockSource)
            {
                Pipelines.Push(_blockPipelineBuilder.Build());
            }
            else
            {
                Pipelines.Push(_transactionPipelineBuilder.Build());
            }
        }
    }
}