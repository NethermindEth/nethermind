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

        public override void EnterTree([NotNull] TreeContext context)
        {
            if(OnStart == null)
            {
                return; 
            }
            
            OnStart();
        }

        public override void EnterSourceExpression([NotNull] SourceExpressionContext context)
        {
            if(OnSourceExpression == null)
            {
                return; 
            }

            OnSourceExpression(context.WORD().GetText());
        }

        public override void EnterWatchExpression([NotNull] WatchExpressionContext context)
        {
            if(OnWatchExpression == null)
            {
                return; 
            }

            OnWatchExpression(context.WORD().GetText());
        }

        public override void EnterWhereExpression([NotNull] WhereExpressionContext context)
        {
            if(OnCondition == null)
            {
                return; 
            }

            ConditionContext condition = context.condition();
            string key = condition.WORD().GetText();
            string booleanOperator = condition.BOOLEAN_OPERATOR().GetText();
            string value = condition.CONDITION_VALUE().GetText();

            OnCondition(key, booleanOperator, value);
        }

        public override void EnterOrCondition([NotNull] OrConditionContext context)
        {
            if(OnOrCondition == null)
            {
                return;
            }

            var condition = context.condition();
            string key = condition.WORD().GetText();
            string booleanOperator = condition.BOOLEAN_OPERATOR().GetText();
            string value = condition.CONDITION_VALUE().GetText(); 

            OnOrCondition(key, booleanOperator, value);
        }

        public override void EnterAndCondition([NotNull] AndConditionContext context)
        {
            if(OnAndCondition == null)
            {
                return;
            }

            var condition = context.condition();
            string key = condition.WORD().GetText();
            string booleanOperator = condition.BOOLEAN_OPERATOR().GetText();
            string value = condition.CONDITION_VALUE().GetText(); 

            OnAndCondition(key, booleanOperator, value);
        }
        public override void EnterPublishExpression([NotNull] PublishExpressionContext context)
        {
            if(OnWatchExpression == null)
            {
                return; 
            }

            OnWatchExpression(context.WORD().GetText());
        }

        public override void ExitTree([NotNull] TreeContext context)
        {
            if(OnExit == null)
            {
                return;
            }

            OnExit();
        }
    }
}