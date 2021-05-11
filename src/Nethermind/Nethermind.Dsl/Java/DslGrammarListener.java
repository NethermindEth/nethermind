// Generated from DslGrammar.g4 by ANTLR 4.9.2
import org.antlr.v4.runtime.tree.ParseTreeListener;

/**
 * This interface defines a complete listener for a parse tree produced by
 * {@link DslGrammarParser}.
 */
public interface DslGrammarListener extends ParseTreeListener {
	/**
	 * Enter a parse tree produced by {@link DslGrammarParser#tree}.
	 * @param ctx the parse tree
	 */
	void enterTree(DslGrammarParser.TreeContext ctx);
	/**
	 * Exit a parse tree produced by {@link DslGrammarParser#tree}.
	 * @param ctx the parse tree
	 */
	void exitTree(DslGrammarParser.TreeContext ctx);
	/**
	 * Enter a parse tree produced by {@link DslGrammarParser#expression}.
	 * @param ctx the parse tree
	 */
	void enterExpression(DslGrammarParser.ExpressionContext ctx);
	/**
	 * Exit a parse tree produced by {@link DslGrammarParser#expression}.
	 * @param ctx the parse tree
	 */
	void exitExpression(DslGrammarParser.ExpressionContext ctx);
	/**
	 * Enter a parse tree produced by {@link DslGrammarParser#sourceExpression}.
	 * @param ctx the parse tree
	 */
	void enterSourceExpression(DslGrammarParser.SourceExpressionContext ctx);
	/**
	 * Exit a parse tree produced by {@link DslGrammarParser#sourceExpression}.
	 * @param ctx the parse tree
	 */
	void exitSourceExpression(DslGrammarParser.SourceExpressionContext ctx);
	/**
	 * Enter a parse tree produced by {@link DslGrammarParser#watchExpression}.
	 * @param ctx the parse tree
	 */
	void enterWatchExpression(DslGrammarParser.WatchExpressionContext ctx);
	/**
	 * Exit a parse tree produced by {@link DslGrammarParser#watchExpression}.
	 * @param ctx the parse tree
	 */
	void exitWatchExpression(DslGrammarParser.WatchExpressionContext ctx);
	/**
	 * Enter a parse tree produced by {@link DslGrammarParser#whereExpression}.
	 * @param ctx the parse tree
	 */
	void enterWhereExpression(DslGrammarParser.WhereExpressionContext ctx);
	/**
	 * Exit a parse tree produced by {@link DslGrammarParser#whereExpression}.
	 * @param ctx the parse tree
	 */
	void exitWhereExpression(DslGrammarParser.WhereExpressionContext ctx);
	/**
	 * Enter a parse tree produced by {@link DslGrammarParser#publishExpression}.
	 * @param ctx the parse tree
	 */
	void enterPublishExpression(DslGrammarParser.PublishExpressionContext ctx);
	/**
	 * Exit a parse tree produced by {@link DslGrammarParser#publishExpression}.
	 * @param ctx the parse tree
	 */
	void exitPublishExpression(DslGrammarParser.PublishExpressionContext ctx);
	/**
	 * Enter a parse tree produced by {@link DslGrammarParser#andCondition}.
	 * @param ctx the parse tree
	 */
	void enterAndCondition(DslGrammarParser.AndConditionContext ctx);
	/**
	 * Exit a parse tree produced by {@link DslGrammarParser#andCondition}.
	 * @param ctx the parse tree
	 */
	void exitAndCondition(DslGrammarParser.AndConditionContext ctx);
	/**
	 * Enter a parse tree produced by {@link DslGrammarParser#orCondition}.
	 * @param ctx the parse tree
	 */
	void enterOrCondition(DslGrammarParser.OrConditionContext ctx);
	/**
	 * Exit a parse tree produced by {@link DslGrammarParser#orCondition}.
	 * @param ctx the parse tree
	 */
	void exitOrCondition(DslGrammarParser.OrConditionContext ctx);
	/**
	 * Enter a parse tree produced by {@link DslGrammarParser#condition}.
	 * @param ctx the parse tree
	 */
	void enterCondition(DslGrammarParser.ConditionContext ctx);
	/**
	 * Exit a parse tree produced by {@link DslGrammarParser#condition}.
	 * @param ctx the parse tree
	 */
	void exitCondition(DslGrammarParser.ConditionContext ctx);
}