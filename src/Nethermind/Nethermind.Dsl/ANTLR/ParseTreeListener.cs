using System;
using System.Linq;
using Antlr4.Runtime.Misc;

namespace Nethermind.Dsl.ANTLR
{
    public class ParseTreeListener : DslGrammarBaseListener
    {
        private AntlrTokenType _tokens;
        public Action<AntlrTokenType, string> OnEnterInit { private get; set; }
        public Action<AntlrTokenType, string> OnEnterExpression { private get; set; }
        public Action<AntlrTokenType> OnEnterCondition { private get; set; }

        public override void EnterInit([NotNull] DslGrammarParser.InitContext context)
        {
            if(OnEnterInit == null)
            {
                return; 
            }

            var sourceNode = context.expression().First();
            var nodeText = sourceNode.OPERATOR().GetText();
            var isTokenType = Enum.IsDefined(typeof(AntlrTokenType), nodeText);
            var tokenValue = sourceNode.OPERATOR_VALUE().GetText();

            if(isTokenType && nodeText.Equals("SOURCE"))
            {
                AntlrTokenType type; 
                AntlrTokenType.TryParse(nodeText, out type);

                OnEnterInit(type, tokenValue);
            }
        }

        public override void EnterExpression([NotNull] DslGrammarParser.ExpressionContext context)
        {
            if(OnEnterExpression == null)
            {
                return;
            }
        }

        public override void EnterCondition([NotNull] DslGrammarParser.ConditionContext context)
        {
            if(OnEnterCondition == null)
            {
                return;
            }
        }
    }
}