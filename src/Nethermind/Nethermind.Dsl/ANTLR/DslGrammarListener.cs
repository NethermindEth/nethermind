using Antlr4.Runtime.Misc;

namespace Nethermind.Dsl.ANTLR
{
    public class DslGrammarListener : DslGrammarBaseListener
    {
        public override void EnterInit([NotNull] DslGrammarParser.InitContext context)
        {
            base.EnterInit(context);
        }

        public override void EnterExpression([NotNull] DslGrammarParser.ExpressionContext context)
        {
            base.EnterExpression(context);
        }

        public override void EnterAssign([NotNull] DslGrammarParser.AssignContext context)
        {
            base.EnterAssign(context);
        }
    }
}