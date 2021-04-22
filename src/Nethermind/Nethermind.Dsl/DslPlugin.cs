using System.Threading.Tasks;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Dsl.ANTLR;

namespace Nethermind.Dsl
{
    public class DslPlugin : INethermindPlugin
    {
        public string Name { get; }
        
        public string Description { get; }
        
        public string Author { get; }

        private DslGrammarListener _listener;

        public Task Init(INethermindApi nethermindApi)
        {
            AntlrInputStream inputStream = new AntlrInputStream();
            DslGrammarLexer lexer = new DslGrammarLexer(inputStream);
            CommonTokenStream tokens = new CommonTokenStream(lexer);
            DslGrammarParser parser = new DslGrammarParser(tokens);
            parser.BuildParseTree = true;
            IParseTree tree = parser.init();

            _listener = new DslGrammarListener();

            ParseTreeWalker.Default.Walk(_listener, tree);
            return Task.CompletedTask;
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
    }
}