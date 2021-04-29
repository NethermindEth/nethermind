using System;
using Antlr4.Runtime.Tree;
using Nethermind.Pipeline;

namespace Nethermind.Dsl.ANTLR
{
    public class Interpreter : IInterpreter
    {
        private readonly IParseTree _tree;
        private readonly ParseTreeListener _treeListener;
        private IPipeline _pipeline;
        private IPipelineBuilder<object, object> _pipelineBuilder;

        public Interpreter(IParseTree tree, ParseTreeListener treeListener)
        {
            _tree = tree ?? throw new ArgumentNullException(nameof(tree));
            _treeListener = treeListener ?? throw new ArgumentNullException(nameof(treeListener));

            _treeListener.OnEnterExpression = AddExpression; 
            _treeListener.OnEnterCondition = AddCondition;
            _treeListener.OnExit = BuildPipeline;
        }

        public void AddSource(string value)
        {
            
        }

        public void AddWatch(string value)
        {

        }
        public void AddExpression(AntlrTokenType tokenType, string value)
        {
            switch (tokenType)
            {
                case AntlrTokenType.SOURCE: 
                    AddSource(value);
                break;
            }
        }
        public void AddCondition(string key, string symbol, string value)
        {

        }

        public void AddPublisher(string value)
        {

        }

        public void BuildPipeline()
        {

        }
    }
}