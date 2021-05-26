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
    // This class will define the DSL Plugin

    public class DslPlugin : INethermindPlugin // Inherits from INethermindPlugin class
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

                var dslScript = await LoadDSLScript(); // The code will only execute after LoadDSLScript finishes reading file

                var inputStream = new AntlrInputStream(dslScript); // Defines an input stream from loaded script
                var lexer = new DslGrammarLexer(inputStream); // Defines a lexer object from the input script
                var tokens = new CommonTokenStream(lexer); // Defines tokens created from ANTLR lexer
                var parser = new DslGrammarParser(tokens); // Defines a parser object based on the lexer output
                parser.BuildParseTree = true; //  Builds parse tree
                IParseTree tree = parser.init(); // Defines a tree object 

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
            _txPool = _api.TxPool; // Defines TxPool and the Block Processor asynchronously
            _blockProcessor = _api.MainBlockProcessor;
            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
            return Task.CompletedTask;  // ?
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask; // Lambda expression, does what ?



        // From this point on only methods are defined and now more operation is undertaken

        private void OnInitEntry(AntlrTokenType tokenType, string tokenValue)
        {
            if (tokenType == AntlrTokenType.SOURCE) // Constructor definition?
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
                    break;                  // Edge case
                case AntlrTokenType.WHERE: 
                    break;                  // Edge case
                case AntlrTokenType.WATCH:
                    SetWatchOnPipeline(tokenValue); // Defines WATCH 
                    break;
                case AntlrTokenType.PUBLISH:
                    AddPublisher(tokenValue); // Publishes
                    break;
                case AntlrTokenType.IS:
                    OnConditionEntry("=",tokenValue);
                    break;
                case AntlrTokenType.NOT:
                    OnConditionEntry("!=", tokenValue);
                default: throw new ArgumentException($"Given token is not supported {tokenType}"); 
            }
        }

        // OnConditionEntry will define all conditional statements such as ==, !=, etc.

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


        // SetWatchOnPipeline will add either a block or a transaction to the pipeline


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

            /* AddPublisher will either add a WebSocketsPublisher block to the pipeline, which will initiate the EthereumJsonSerializer, 
                or will add a LogPublisher block which will initialize the LogManager as well as the EthereumJsonSerializer */

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

            // Build pipeline instantiates the blockProcessorPipelineBuilder class and calls the Build() method to create a pipeline object

            private void BuildPipeline()
            {
                _pipeline = _blockProcessorPipelineBuilder.Build();
            }


            // Loads script located at specified directory and awaits for  text to be read

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


    /* Notes:

    This plugin follows the Task Asynchronous Programming (TAP) model. The goal of this, according to Microsoft, is
    to 'enable code that reads like a sentence, but executes in a much more complicated order based on external 
    resource allocation and when tasks complete". Below are some important components of this model.

    async: represents a single no-return operation. The modifier signifies to the compiler that this method contains
           an await statement, and therefore an asynchronous operation.

    await: suspends evaluation of async until the async operations represented by its operand completes. This
           means that any task that is asynchronous is suspended until all operations are finished. You await each Task 
           before using its result. 
    
    Task : the Task class represents a single operation that does not return a value and usually executes asynchronously. 
           Wihout the async keyword, the compiler does not automatically generate the code need top create the async state
           machine and return a Task. Without it, the Task returnn type must be manually defined.


    Suggestions

    Line 244: "async Task" is redundant as async keyword will automatically define return typ
    
    C# syntax and properties:

    '_' indicates private field

    { get; set: } define automatic properties that do not need a field, only get is read-only, only set is write-only

    */