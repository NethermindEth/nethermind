using Antlr4.Runtime;

namespace Nethermind.Dsl
{
    public class ParseTreeBuilder
    {
        private readonly AntlrInputStream _inputStream;
        private readonly DslGrammarLexer _lexer;
        private readonly CommonTokenStream _tokenStream;
        private readonly DslGrammarParser _parser;
        private readonly ParseTreeBuilder _treeBuilder;

        public ParseTreeBuilder()
        {
            _inputStream = new AntlrInputStream();
            _lexer = new DslGrammarLexer(_inputStream);
            _tokenStream = new CommonTokenStream(_lexer);
            _parser = new DslGrammarParser(_tokenStream);
            var expression = _parser.expression();
        }
    }
}