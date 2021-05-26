// Generated from DslGrammar.g4 by ANTLR 4.9.2
import org.antlr.v4.runtime.tree.ParseTreeListener;

/**
 * This interface defines a complete listener for a parse tree produced by
 * {@link DslGrammarParser}.
 */
public interface DslGrammarListener extends ParseTreeListener {
	/**
	 * Enter a parse tree produced by {@link DslGrammarParser#init}.
	 * @param ctx the parse tree
	 */
	void enterInit(DslGrammarParser.InitContext ctx);
	/**
	 * Exit a parse tree produced by {@link DslGrammarParser#init}.
	 * @param ctx the parse tree
	 */
	void exitInit(DslGrammarParser.InitContext ctx);
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