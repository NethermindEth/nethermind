using System;
using System.Linq;
using Antlr4.Runtime.Misc;

namespace Nethermind.Dsl.ANTLR
{
    public class ParseTreeListener : DslGrammarBaseListener
    {
        private AntlrTokenType _tokens;
        public Action<AntlrTokenType, string> OnStart { private get; set; }
        public Action<AntlrTokenType, string> OnExpression { private get; set; }
        public Action<string> OnSourceExpression { private get; set; }
        public Action<string> OnWatchExpression { private get; set; }
        public Action<string> OnPublishExpression { private get; set; }
        public Action<string, string, string> OnCondition { private get; set; }
        public Action<string, string, string> OnOrCondition { private get; set; }
        public Action<string, string, string> OnAndCondition { private get; set; }
        public Action OnExit { private get; set; }

        public override void EnterExpression([NotNull] DslGrammarParser.ExpressionContext context)
        {
            // if(OnEnterExpression == null)
            // {
            //     return;
            // }

            // AntlrTokenType tokenType = (AntlrTokenType)Enum.Parse(typeof(AntlrTokenType), context.OPERATOR().GetText());
            // OnEnterExpression(tokenType, context.WORD().GetText());
        }

        public override void EnterCondition([NotNull] DslGrammarParser.ConditionContext context)
        {
            if(OnCondition == null)
            {
                return;
            }

            OnCondition(context.WORD().First().GetText(), context.ARITHMETIC_SYMBOL().GetText(), context.ADDRESS().GetText());
        }

        public override void EnterSourceExpression([NotNull] DslGrammarParser.SourceExpressionContext context)
        {
            if(OnSourceExpression == null)
            {
                return; 
            }

            OnSourceExpression(context.WORD().GetText());
        }

        public override void EnterWatchExpression([NotNull] DslGrammarParser.WatchExpressionContext context)
        {
            if(OnWatchExpression == null)
            {
                return; 
            }

            OnWatchExpression(context.WORD().GetText());
        }

        public override void EnterWhereExpression([NotNull] DslGrammarParser.WhereExpressionContext context)
        {
            if(OnCondition == null)
            {
                return; 
            }

            OnCondition(context.condition().WORD().First().GetText(), context.condition().;
        }

        public override void EnterPublishExpression([NotNull] DslGrammarParser.PublishExpressionContext context)
        {
            if(OnWatchExpression == null)
            {
                return; 
            }

            OnWatchExpression(context.WORD().GetText());
        }

        public override void ExitTree([NotNull] DslGrammarParser.TreeContext context)
        {
            base.ExitTree(context);
        }
    }
}