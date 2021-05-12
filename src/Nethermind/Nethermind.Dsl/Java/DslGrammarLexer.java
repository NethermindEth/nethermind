// Generated from DslGrammar.g4 by ANTLR 4.9.2
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
	static { RuntimeMetaData.checkVersion("4.9.2", RuntimeMetaData.VERSION); }

	protected static final DFA[] _decisionToDFA;
	protected static final PredictionContextCache _sharedContextCache =
		new PredictionContextCache();
	public static final int
		BOOLEAN_OPERATOR=1, ARITHMETIC_SYMBOL=2, SOURCE=3, WATCH=4, WHERE=5, PUBLISH=6, 
		AND=7, OR=8, CONTAINS=9, PUBLISH_VALUE=10, WEBSOCKETS=11, LOG_PUBLISHER=12, 
		WORD=13, BYTECODE=14, DIGIT=15, ADDRESS=16, WS=17;
	public static String[] channelNames = {
		"DEFAULT_TOKEN_CHANNEL", "HIDDEN"
	};

	public static String[] modeNames = {
		"DEFAULT_MODE"
	};

	private static String[] makeRuleNames() {
		return new String[] {
			"BOOLEAN_OPERATOR", "ARITHMETIC_SYMBOL", "SOURCE", "WATCH", "WHERE", 
			"PUBLISH", "AND", "OR", "CONTAINS", "PUBLISH_VALUE", "WEBSOCKETS", "LOG_PUBLISHER", 
			"WORD", "BYTECODE", "DIGIT", "ADDRESS", "WS"
		};
	}
	public static final String[] ruleNames = makeRuleNames();

	private static String[] makeLiteralNames() {
		return new String[] {
			null, null, null, "'SOURCE'", "'WATCH'", "'WHERE'", "'PUBLISH'", "'AND'", 
			"'OR'", "'CONTAINS'"
		};
	}
	private static final String[] _LITERAL_NAMES = makeLiteralNames();
	private static String[] makeSymbolicNames() {
		return new String[] {
			null, "BOOLEAN_OPERATOR", "ARITHMETIC_SYMBOL", "SOURCE", "WATCH", "WHERE", 
			"PUBLISH", "AND", "OR", "CONTAINS", "PUBLISH_VALUE", "WEBSOCKETS", "LOG_PUBLISHER", 
			"WORD", "BYTECODE", "DIGIT", "ADDRESS", "WS"
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
		"\3\u608b\ua72a\u8133\ub9ed\u417c\u3be7\u7786\u5964\2\23\u00c8\b\1\4\2"+
		"\t\2\4\3\t\3\4\4\t\4\4\5\t\5\4\6\t\6\4\7\t\7\4\b\t\b\4\t\t\t\4\n\t\n\4"+
		"\13\t\13\4\f\t\f\4\r\t\r\4\16\t\16\4\17\t\17\4\20\t\20\4\21\t\21\4\22"+
		"\t\22\3\2\3\2\5\2(\n\2\3\3\3\3\3\3\3\3\3\3\3\3\3\3\3\3\3\3\5\3\63\n\3"+
		"\3\4\3\4\3\4\3\4\3\4\3\4\3\4\3\5\3\5\3\5\3\5\3\5\3\5\3\6\3\6\3\6\3\6\3"+
		"\6\3\6\3\7\3\7\3\7\3\7\3\7\3\7\3\7\3\7\3\b\3\b\3\b\3\b\3\t\3\t\3\t\3\n"+
		"\3\n\3\n\3\n\3\n\3\n\3\n\3\n\3\n\3\13\3\13\5\13b\n\13\3\f\3\f\3\f\3\f"+
		"\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3"+
		"\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\5\f\u0082\n\f\3\r\3\r\3\r\3\r\3\r\3"+
		"\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r"+
		"\3\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r\5\r\u00a8\n\r\3\16"+
		"\6\16\u00ab\n\16\r\16\16\16\u00ac\3\17\6\17\u00b0\n\17\r\17\16\17\u00b1"+
		"\3\20\6\20\u00b5\n\20\r\20\16\20\u00b6\3\21\3\21\3\21\3\21\7\21\u00bd"+
		"\n\21\f\21\16\21\u00c0\13\21\3\22\6\22\u00c3\n\22\r\22\16\22\u00c4\3\22"+
		"\3\22\2\2\23\3\3\5\4\7\5\t\6\13\7\r\b\17\t\21\n\23\13\25\f\27\r\31\16"+
		"\33\17\35\20\37\21!\22#\23\3\2\7\4\2>>@@\4\2C\\c|\5\2\62;CHch\3\2\62;"+
		"\5\2\13\f\17\17\"\"\2\u00d6\2\3\3\2\2\2\2\5\3\2\2\2\2\7\3\2\2\2\2\t\3"+
		"\2\2\2\2\13\3\2\2\2\2\r\3\2\2\2\2\17\3\2\2\2\2\21\3\2\2\2\2\23\3\2\2\2"+
		"\2\25\3\2\2\2\2\27\3\2\2\2\2\31\3\2\2\2\2\33\3\2\2\2\2\35\3\2\2\2\2\37"+
		"\3\2\2\2\2!\3\2\2\2\2#\3\2\2\2\3\'\3\2\2\2\5\62\3\2\2\2\7\64\3\2\2\2\t"+
		";\3\2\2\2\13A\3\2\2\2\rG\3\2\2\2\17O\3\2\2\2\21S\3\2\2\2\23V\3\2\2\2\25"+
		"a\3\2\2\2\27\u0081\3\2\2\2\31\u00a7\3\2\2\2\33\u00aa\3\2\2\2\35\u00af"+
		"\3\2\2\2\37\u00b4\3\2\2\2!\u00b8\3\2\2\2#\u00c2\3\2\2\2%(\5\5\3\2&(\5"+
		"\23\n\2\'%\3\2\2\2\'&\3\2\2\2(\4\3\2\2\2)*\7?\2\2*\63\7?\2\2+,\7#\2\2"+
		",\63\7?\2\2-\63\t\2\2\2./\7>\2\2/\63\7?\2\2\60\61\7@\2\2\61\63\7?\2\2"+
		"\62)\3\2\2\2\62+\3\2\2\2\62-\3\2\2\2\62.\3\2\2\2\62\60\3\2\2\2\63\6\3"+
		"\2\2\2\64\65\7U\2\2\65\66\7Q\2\2\66\67\7W\2\2\678\7T\2\289\7E\2\29:\7"+
		"G\2\2:\b\3\2\2\2;<\7Y\2\2<=\7C\2\2=>\7V\2\2>?\7E\2\2?@\7J\2\2@\n\3\2\2"+
		"\2AB\7Y\2\2BC\7J\2\2CD\7G\2\2DE\7T\2\2EF\7G\2\2F\f\3\2\2\2GH\7R\2\2HI"+
		"\7W\2\2IJ\7D\2\2JK\7N\2\2KL\7K\2\2LM\7U\2\2MN\7J\2\2N\16\3\2\2\2OP\7C"+
		"\2\2PQ\7P\2\2QR\7F\2\2R\20\3\2\2\2ST\7Q\2\2TU\7T\2\2U\22\3\2\2\2VW\7E"+
		"\2\2WX\7Q\2\2XY\7P\2\2YZ\7V\2\2Z[\7C\2\2[\\\7K\2\2\\]\7P\2\2]^\7U\2\2"+
		"^\24\3\2\2\2_b\5\27\f\2`b\5\31\r\2a_\3\2\2\2a`\3\2\2\2b\26\3\2\2\2cd\7"+
		"Y\2\2de\7g\2\2ef\7d\2\2fg\7U\2\2gh\7q\2\2hi\7e\2\2ij\7m\2\2jk\7g\2\2k"+
		"l\7v\2\2l\u0082\7u\2\2mn\7y\2\2no\7g\2\2op\7d\2\2pq\7U\2\2qr\7q\2\2rs"+
		"\7e\2\2st\7m\2\2tu\7g\2\2uv\7v\2\2v\u0082\7u\2\2wx\7y\2\2xy\7g\2\2yz\7"+
		"d\2\2z{\7u\2\2{|\7q\2\2|}\7e\2\2}~\7m\2\2~\177\7g\2\2\177\u0080\7v\2\2"+
		"\u0080\u0082\7u\2\2\u0081c\3\2\2\2\u0081m\3\2\2\2\u0081w\3\2\2\2\u0082"+
		"\30\3\2\2\2\u0083\u0084\7N\2\2\u0084\u0085\7q\2\2\u0085\u0086\7i\2\2\u0086"+
		"\u0087\7R\2\2\u0087\u0088\7w\2\2\u0088\u0089\7d\2\2\u0089\u008a\7n\2\2"+
		"\u008a\u008b\7k\2\2\u008b\u008c\7u\2\2\u008c\u008d\7j\2\2\u008d\u008e"+
		"\7g\2\2\u008e\u00a8\7t\2\2\u008f\u0090\7n\2\2\u0090\u0091\7q\2\2\u0091"+
		"\u0092\7i\2\2\u0092\u0093\7R\2\2\u0093\u0094\7w\2\2\u0094\u0095\7d\2\2"+
		"\u0095\u0096\7n\2\2\u0096\u0097\7k\2\2\u0097\u0098\7u\2\2\u0098\u0099"+
		"\7j\2\2\u0099\u009a\7g\2\2\u009a\u00a8\7t\2\2\u009b\u009c\7n\2\2\u009c"+
		"\u009d\7q\2\2\u009d\u009e\7i\2\2\u009e\u009f\7r\2\2\u009f\u00a0\7w\2\2"+
		"\u00a0\u00a1\7d\2\2\u00a1\u00a2\7n\2\2\u00a2\u00a3\7k\2\2\u00a3\u00a4"+
		"\7u\2\2\u00a4\u00a5\7j\2\2\u00a5\u00a6\7g\2\2\u00a6\u00a8\7t\2\2\u00a7"+
		"\u0083\3\2\2\2\u00a7\u008f\3\2\2\2\u00a7\u009b\3\2\2\2\u00a8\32\3\2\2"+
		"\2\u00a9\u00ab\t\3\2\2\u00aa\u00a9\3\2\2\2\u00ab\u00ac\3\2\2\2\u00ac\u00aa"+
		"\3\2\2\2\u00ac\u00ad\3\2\2\2\u00ad\34\3\2\2\2\u00ae\u00b0\t\4\2\2\u00af"+
		"\u00ae\3\2\2\2\u00b0\u00b1\3\2\2\2\u00b1\u00af\3\2\2\2\u00b1\u00b2\3\2"+
		"\2\2\u00b2\36\3\2\2\2\u00b3\u00b5\t\5\2\2\u00b4\u00b3\3\2\2\2\u00b5\u00b6"+
		"\3\2\2\2\u00b6\u00b4\3\2\2\2\u00b6\u00b7\3\2\2\2\u00b7 \3\2\2\2\u00b8"+
		"\u00b9\7\62\2\2\u00b9\u00ba\7z\2\2\u00ba\u00be\3\2\2\2\u00bb\u00bd\t\4"+
		"\2\2\u00bc\u00bb\3\2\2\2\u00bd\u00c0\3\2\2\2\u00be\u00bc\3\2\2\2\u00be"+
		"\u00bf\3\2\2\2\u00bf\"\3\2\2\2\u00c0\u00be\3\2\2\2\u00c1\u00c3\t\6\2\2"+
		"\u00c2\u00c1\3\2\2\2\u00c3\u00c4\3\2\2\2\u00c4\u00c2\3\2\2\2\u00c4\u00c5"+
		"\3\2\2\2\u00c5\u00c6\3\2\2\2\u00c6\u00c7\b\22\2\2\u00c7$\3\2\2\2\r\2\'"+
		"\62a\u0081\u00a7\u00ac\u00b1\u00b6\u00be\u00c4\3\b\2\2";
	public static final ATN _ATN =
		new ATNDeserializer().deserialize(_serializedATN.toCharArray());
	static {
		_decisionToDFA = new DFA[_ATN.getNumberOfDecisions()];
		for (int i = 0; i < _ATN.getNumberOfDecisions(); i++) {
			_decisionToDFA[i] = new DFA(_ATN.getDecisionState(i), i);
		}
	}
}