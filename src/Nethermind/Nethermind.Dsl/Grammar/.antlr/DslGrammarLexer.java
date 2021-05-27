// Generated from /home/gheise/nethermind/src/Nethermind/Nethermind.Dsl/Grammar/DslGrammar.g4 by ANTLR 4.8
import org.antlr.v4.runtime.Lexer;
import org.antlr.v4.runtime.CharStream;
import org.antlr.v4.runtime.Token;
import org.antlr.v4.runtime.TokenStream;
import org.antlr.v4.runtime.*;
import org.antlr.v4.runtime.atn.*;
import org.antlr.v4.runtime.dfa.DFA;
import org.antlr.v4.runtime.misc.*;

@SuppressWarnings({"all", "warnings", "unchecked", "unused", "cast"})
public class DslGrammarLexer extends Lexer {
	static { RuntimeMetaData.checkVersion("4.8", RuntimeMetaData.VERSION); }

	protected static final DFA[] _decisionToDFA;
	protected static final PredictionContextCache _sharedContextCache =
		new PredictionContextCache();
	public static final int
		BOOLEAN_OPERATOR=1, ARITHMETIC_SYMBOL=2, SOURCE=3, WATCH=4, WHERE=5, PUBLISH=6, 
		AND=7, OR=8, CONTAINS=9, IS=10, NOT=11, PUBLISH_VALUE=12, WEBSOCKETS=13, 
		LOG_PUBLISHER=14, WORD=15, BYTECODE=16, DIGIT=17, ADDRESS=18, WS=19;
	public static String[] channelNames = {
		"DEFAULT_TOKEN_CHANNEL", "HIDDEN"
	};

	public static String[] modeNames = {
		"DEFAULT_MODE"
	};

	private static String[] makeRuleNames() {
		return new String[] {
			"BOOLEAN_OPERATOR", "ARITHMETIC_SYMBOL", "SOURCE", "WATCH", "WHERE", 
			"PUBLISH", "AND", "OR", "CONTAINS", "IS", "NOT", "PUBLISH_VALUE", "WEBSOCKETS", 
			"LOG_PUBLISHER", "WORD", "BYTECODE", "DIGIT", "ADDRESS", "WS"
		};
	}
	public static final String[] ruleNames = makeRuleNames();

	private static String[] makeLiteralNames() {
		return new String[] {
			null, null, null, "'SOURCE'", "'WATCH'", "'WHERE'", "'PUBLISH'", "'AND'", 
			"'OR'", "'CONTAINS'", "'IS'", "'NOT'"
		};
	}
	private static final String[] _LITERAL_NAMES = makeLiteralNames();
	private static String[] makeSymbolicNames() {
		return new String[] {
			null, "BOOLEAN_OPERATOR", "ARITHMETIC_SYMBOL", "SOURCE", "WATCH", "WHERE", 
			"PUBLISH", "AND", "OR", "CONTAINS", "IS", "NOT", "PUBLISH_VALUE", "WEBSOCKETS", 
			"LOG_PUBLISHER", "WORD", "BYTECODE", "DIGIT", "ADDRESS", "WS"
		};
	}
	private static final String[] _SYMBOLIC_NAMES = makeSymbolicNames();
	public static final Vocabulary VOCABULARY = new VocabularyImpl(_LITERAL_NAMES, _SYMBOLIC_NAMES);

	/**
	 * @deprecated Use {@link #VOCABULARY} instead.
	 */
	@Deprecated
	public static final String[] tokenNames;
	static {
		tokenNames = new String[_SYMBOLIC_NAMES.length];
		for (int i = 0; i < tokenNames.length; i++) {
			tokenNames[i] = VOCABULARY.getLiteralName(i);
			if (tokenNames[i] == null) {
				tokenNames[i] = VOCABULARY.getSymbolicName(i);
			}

			if (tokenNames[i] == null) {
				tokenNames[i] = "<INVALID>";
			}
		}
	}

	@Override
	@Deprecated
	public String[] getTokenNames() {
		return tokenNames;
	}

	@Override

	public Vocabulary getVocabulary() {
		return VOCABULARY;
	}


	public DslGrammarLexer(CharStream input) {
		super(input);
		_interp = new LexerATNSimulator(this,_ATN,_decisionToDFA,_sharedContextCache);
	}

	@Override
	public String getGrammarFileName() { return "DslGrammar.g4"; }

	@Override
	public String[] getRuleNames() { return ruleNames; }

	@Override
	public String getSerializedATN() { return _serializedATN; }

	@Override
	public String[] getChannelNames() { return channelNames; }

	@Override
	public String[] getModeNames() { return modeNames; }

	@Override
	public ATN getATN() { return _ATN; }

	public static final String _serializedATN =
		"\3\u608b\ua72a\u8133\ub9ed\u417c\u3be7\u7786\u5964\2\25\u00d1\b\1\4\2"+
		"\t\2\4\3\t\3\4\4\t\4\4\5\t\5\4\6\t\6\4\7\t\7\4\b\t\b\4\t\t\t\4\n\t\n\4"+
		"\13\t\13\4\f\t\f\4\r\t\r\4\16\t\16\4\17\t\17\4\20\t\20\4\21\t\21\4\22"+
		"\t\22\4\23\t\23\4\24\t\24\3\2\3\2\5\2,\n\2\3\3\3\3\3\3\3\3\3\3\3\3\3\3"+
		"\5\3\65\n\3\3\4\3\4\3\4\3\4\3\4\3\4\3\4\3\5\3\5\3\5\3\5\3\5\3\5\3\6\3"+
		"\6\3\6\3\6\3\6\3\6\3\7\3\7\3\7\3\7\3\7\3\7\3\7\3\7\3\b\3\b\3\b\3\b\3\t"+
		"\3\t\3\t\3\n\3\n\3\n\3\n\3\n\3\n\3\n\3\n\3\n\3\13\3\13\3\13\3\f\3\f\3"+
		"\f\3\f\3\r\3\r\5\rk\n\r\3\16\3\16\3\16\3\16\3\16\3\16\3\16\3\16\3\16\3"+
		"\16\3\16\3\16\3\16\3\16\3\16\3\16\3\16\3\16\3\16\3\16\3\16\3\16\3\16\3"+
		"\16\3\16\3\16\3\16\3\16\3\16\3\16\5\16\u008b\n\16\3\17\3\17\3\17\3\17"+
		"\3\17\3\17\3\17\3\17\3\17\3\17\3\17\3\17\3\17\3\17\3\17\3\17\3\17\3\17"+
		"\3\17\3\17\3\17\3\17\3\17\3\17\3\17\3\17\3\17\3\17\3\17\3\17\3\17\3\17"+
		"\3\17\3\17\3\17\3\17\5\17\u00b1\n\17\3\20\6\20\u00b4\n\20\r\20\16\20\u00b5"+
		"\3\21\6\21\u00b9\n\21\r\21\16\21\u00ba\3\22\6\22\u00be\n\22\r\22\16\22"+
		"\u00bf\3\23\3\23\3\23\3\23\7\23\u00c6\n\23\f\23\16\23\u00c9\13\23\3\24"+
		"\6\24\u00cc\n\24\r\24\16\24\u00cd\3\24\3\24\2\2\25\3\3\5\4\7\5\t\6\13"+
		"\7\r\b\17\t\21\n\23\13\25\f\27\r\31\16\33\17\35\20\37\21!\22#\23%\24\'"+
		"\25\3\2\7\4\2>>@@\4\2C\\c|\5\2\62;CHch\3\2\62;\5\2\13\f\17\17\"\"\2\u00df"+
		"\2\3\3\2\2\2\2\5\3\2\2\2\2\7\3\2\2\2\2\t\3\2\2\2\2\13\3\2\2\2\2\r\3\2"+
		"\2\2\2\17\3\2\2\2\2\21\3\2\2\2\2\23\3\2\2\2\2\25\3\2\2\2\2\27\3\2\2\2"+
		"\2\31\3\2\2\2\2\33\3\2\2\2\2\35\3\2\2\2\2\37\3\2\2\2\2!\3\2\2\2\2#\3\2"+
		"\2\2\2%\3\2\2\2\2\'\3\2\2\2\3+\3\2\2\2\5\64\3\2\2\2\7\66\3\2\2\2\t=\3"+
		"\2\2\2\13C\3\2\2\2\rI\3\2\2\2\17Q\3\2\2\2\21U\3\2\2\2\23X\3\2\2\2\25a"+
		"\3\2\2\2\27d\3\2\2\2\31j\3\2\2\2\33\u008a\3\2\2\2\35\u00b0\3\2\2\2\37"+
		"\u00b3\3\2\2\2!\u00b8\3\2\2\2#\u00bd\3\2\2\2%\u00c1\3\2\2\2\'\u00cb\3"+
		"\2\2\2),\5\5\3\2*,\5\23\n\2+)\3\2\2\2+*\3\2\2\2,\4\3\2\2\2-\65\5\25\13"+
		"\2.\65\5\27\f\2/\65\t\2\2\2\60\61\7>\2\2\61\65\7?\2\2\62\63\7@\2\2\63"+
		"\65\7?\2\2\64-\3\2\2\2\64.\3\2\2\2\64/\3\2\2\2\64\60\3\2\2\2\64\62\3\2"+
		"\2\2\65\6\3\2\2\2\66\67\7U\2\2\678\7Q\2\289\7W\2\29:\7T\2\2:;\7E\2\2;"+
		"<\7G\2\2<\b\3\2\2\2=>\7Y\2\2>?\7C\2\2?@\7V\2\2@A\7E\2\2AB\7J\2\2B\n\3"+
		"\2\2\2CD\7Y\2\2DE\7J\2\2EF\7G\2\2FG\7T\2\2GH\7G\2\2H\f\3\2\2\2IJ\7R\2"+
		"\2JK\7W\2\2KL\7D\2\2LM\7N\2\2MN\7K\2\2NO\7U\2\2OP\7J\2\2P\16\3\2\2\2Q"+
		"R\7C\2\2RS\7P\2\2ST\7F\2\2T\20\3\2\2\2UV\7Q\2\2VW\7T\2\2W\22\3\2\2\2X"+
		"Y\7E\2\2YZ\7Q\2\2Z[\7P\2\2[\\\7V\2\2\\]\7C\2\2]^\7K\2\2^_\7P\2\2_`\7U"+
		"\2\2`\24\3\2\2\2ab\7K\2\2bc\7U\2\2c\26\3\2\2\2de\7P\2\2ef\7Q\2\2fg\7V"+
		"\2\2g\30\3\2\2\2hk\5\33\16\2ik\5\35\17\2jh\3\2\2\2ji\3\2\2\2k\32\3\2\2"+
		"\2lm\7Y\2\2mn\7g\2\2no\7d\2\2op\7U\2\2pq\7q\2\2qr\7e\2\2rs\7m\2\2st\7"+
		"g\2\2tu\7v\2\2u\u008b\7u\2\2vw\7y\2\2wx\7g\2\2xy\7d\2\2yz\7U\2\2z{\7q"+
		"\2\2{|\7e\2\2|}\7m\2\2}~\7g\2\2~\177\7v\2\2\177\u008b\7u\2\2\u0080\u0081"+
		"\7y\2\2\u0081\u0082\7g\2\2\u0082\u0083\7d\2\2\u0083\u0084\7u\2\2\u0084"+
		"\u0085\7q\2\2\u0085\u0086\7e\2\2\u0086\u0087\7m\2\2\u0087\u0088\7g\2\2"+
		"\u0088\u0089\7v\2\2\u0089\u008b\7u\2\2\u008al\3\2\2\2\u008av\3\2\2\2\u008a"+
		"\u0080\3\2\2\2\u008b\34\3\2\2\2\u008c\u008d\7N\2\2\u008d\u008e\7q\2\2"+
		"\u008e\u008f\7i\2\2\u008f\u0090\7R\2\2\u0090\u0091\7w\2\2\u0091\u0092"+
		"\7d\2\2\u0092\u0093\7n\2\2\u0093\u0094\7k\2\2\u0094\u0095\7u\2\2\u0095"+
		"\u0096\7j\2\2\u0096\u0097\7g\2\2\u0097\u00b1\7t\2\2\u0098\u0099\7n\2\2"+
		"\u0099\u009a\7q\2\2\u009a\u009b\7i\2\2\u009b\u009c\7R\2\2\u009c\u009d"+
		"\7w\2\2\u009d\u009e\7d\2\2\u009e\u009f\7n\2\2\u009f\u00a0\7k\2\2\u00a0"+
		"\u00a1\7u\2\2\u00a1\u00a2\7j\2\2\u00a2\u00a3\7g\2\2\u00a3\u00b1\7t\2\2"+
		"\u00a4\u00a5\7n\2\2\u00a5\u00a6\7q\2\2\u00a6\u00a7\7i\2\2\u00a7\u00a8"+
		"\7r\2\2\u00a8\u00a9\7w\2\2\u00a9\u00aa\7d\2\2\u00aa\u00ab\7n\2\2\u00ab"+
		"\u00ac\7k\2\2\u00ac\u00ad\7u\2\2\u00ad\u00ae\7j\2\2\u00ae\u00af\7g\2\2"+
		"\u00af\u00b1\7t\2\2\u00b0\u008c\3\2\2\2\u00b0\u0098\3\2\2\2\u00b0\u00a4"+
		"\3\2\2\2\u00b1\36\3\2\2\2\u00b2\u00b4\t\3\2\2\u00b3\u00b2\3\2\2\2\u00b4"+
		"\u00b5\3\2\2\2\u00b5\u00b3\3\2\2\2\u00b5\u00b6\3\2\2\2\u00b6 \3\2\2\2"+
		"\u00b7\u00b9\t\4\2\2\u00b8\u00b7\3\2\2\2\u00b9\u00ba\3\2\2\2\u00ba\u00b8"+
		"\3\2\2\2\u00ba\u00bb\3\2\2\2\u00bb\"\3\2\2\2\u00bc\u00be\t\5\2\2\u00bd"+
		"\u00bc\3\2\2\2\u00be\u00bf\3\2\2\2\u00bf\u00bd\3\2\2\2\u00bf\u00c0\3\2"+
		"\2\2\u00c0$\3\2\2\2\u00c1\u00c2\7\62\2\2\u00c2\u00c3\7z\2\2\u00c3\u00c7"+
		"\3\2\2\2\u00c4\u00c6\t\4\2\2\u00c5\u00c4\3\2\2\2\u00c6\u00c9\3\2\2\2\u00c7"+
		"\u00c5\3\2\2\2\u00c7\u00c8\3\2\2\2\u00c8&\3\2\2\2\u00c9\u00c7\3\2\2\2"+
		"\u00ca\u00cc\t\6\2\2\u00cb\u00ca\3\2\2\2\u00cc\u00cd\3\2\2\2\u00cd\u00cb"+
		"\3\2\2\2\u00cd\u00ce\3\2\2\2\u00ce\u00cf\3\2\2\2\u00cf\u00d0\b\24\2\2"+
		"\u00d0(\3\2\2\2\r\2+\64j\u008a\u00b0\u00b5\u00ba\u00bf\u00c7\u00cd\3\b"+
		"\2\2";
	public static final ATN _ATN =
		new ATNDeserializer().deserialize(_serializedATN.toCharArray());
	static {
		_decisionToDFA = new DFA[_ATN.getNumberOfDecisions()];
		for (int i = 0; i < _ATN.getNumberOfDecisions(); i++) {
			_decisionToDFA[i] = new DFA(_ATN.getDecisionState(i), i);
		}
	}
}