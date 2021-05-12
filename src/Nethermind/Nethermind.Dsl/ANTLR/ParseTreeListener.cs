using System;
using System.Linq;
using Antlr4.Runtime.Misc;
using static DslGrammarParser;

namespace Nethermind.Dsl.ANTLR
{
    public class ParseTreeListener : DslGrammarBaseListener
    {
        public Action OnStart { private get; set; }
        public Action<string> OnSourceExpression { private get; set; }
        public Action<string> OnWatchExpression { private get; set; }
        public Action<string> OnPublishExpression { private get; set; }
        public Action<string, string, string> OnCondition { private get; set; }
        public Action<string, string, string> OnOrCondition { private get; set; }
        public Action<string, string, string> OnAndCondition { private get; set; }
        public Action OnExit { private get; set; }

        public override void EnterSourceExpression([NotNull] SourceExpressionContext context)
        {
            if (OnSourceExpression == null)
            {
                return;
            }

            OnSourceExpression(context.WORD().GetText());
        }

        public override void EnterWatchExpression([NotNull] WatchExpressionContext context)
        {
            if (OnWatchExpression == null)
            {
                return;
            }

            OnWatchExpression(context.WORD().GetText());
        }

        public override void EnterWhereExpression([NotNull] WhereExpressionContext context)
        {
            if (OnCondition == null)
            {
                return;
            }

            ConditionContext condition = context.condition();
            string key = condition.WORD().First().GetText();
            string booleanOperator = condition.BOOLEAN_OPERATOR().GetText();

            if(condition.DIGIT() != null)
            {
                OnCondition(key, booleanOperator, condition.DIGIT().GetText());
            }
            else if(condition.WORD()[1] != null)
            {
                OnCondition(key, booleanOperator, condition.WORD()[1].GetText());
            }
            else if(condition.ADDRESS() != null)
            {
                OnCondition(key, booleanOperator, condition.ADDRESS().GetText());
            }
        }

        public override void EnterOrCondition([NotNull] OrConditionContext context)
        {
            if (OnOrCondition == null)
            {
                return;
            }

            var condition = context.condition();
            string key = condition.WORD().First().GetText();
            string booleanOperator = condition.BOOLEAN_OPERATOR().GetText();

            if(condition.DIGIT() != null)
            {
                OnOrCondition(key, booleanOperator, condition.DIGIT().GetText());
            }
            else if(condition.WORD()[1] != null)
            {
                OnOrCondition(key, booleanOperator, condition.WORD()[1].GetText());
            }
            else if(condition.ADDRESS() != null)
            {
                OnOrCondition(key, booleanOperator, condition.ADDRESS().GetText());
            }
        }

        public override void EnterAndCondition([NotNull] AndConditionContext context)
        {
            if (OnAndCondition == null)
            {
                return;
            }

            var condition = context.condition();
            string key = condition.WORD().First().GetText();
            string booleanOperator = condition.BOOLEAN_OPERATOR().GetText();

            if(condition.DIGIT() != null)
            {
                OnAndCondition(key, booleanOperator, condition.DIGIT().GetText());
            }
            else if(condition.WORD()[1] != null)
            {
                OnAndCondition(key, booleanOperator, condition.WORD()[1].GetText());
            }
            else if(condition.ADDRESS() != null)
            {
                OnAndCondition(key, booleanOperator, condition.ADDRESS().GetText());
            }
        }
        public override void EnterPublishExpression([NotNull] PublishExpressionContext context)
        {
            if (OnWatchExpression == null)
            {
                return;
            }

            OnWatchExpression(context.WORD().GetText());
        }
    }
}