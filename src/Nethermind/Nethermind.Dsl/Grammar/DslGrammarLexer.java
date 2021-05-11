// Generated from ../Grammar/DslGrammar.g4 by ANTLR 4.9.2
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
		WORD=1, DIGIT=2, ADDRESS=3, WS=4, BOOLEAN_OPERATOR=5, ARITHMETIC_SYMBOL=6, 
		CONDITION_VALUE=7, SOURCE=8, WATCH=9, WHERE=10, PUBLISH=11, AND=12, OR=13, 
		CONTAINS=14, PUBLISH_VALUE=15, WEBSOCKETS=16, LOG_PUBLISHER=17;
	public static String[] channelNames = {
		"DEFAULT_TOKEN_CHANNEL", "HIDDEN"
	};

	public static String[] modeNames = {
		"DEFAULT_MODE"
	};

	private static String[] makeRuleNames() {
		return new String[] {
			"WORD", "DIGIT", "ADDRESS", "WS", "BOOLEAN_OPERATOR", "ARITHMETIC_SYMBOL", 
			"CONDITION_VALUE", "SOURCE", "WATCH", "WHERE", "PUBLISH", "AND", "OR", 
			"CONTAINS", "PUBLISH_VALUE", "WEBSOCKETS", "LOG_PUBLISHER"
		};
	}
	public static final String[] ruleNames = makeRuleNames();

	private static String[] makeLiteralNames() {
		return new String[] {
			null, null, null, null, null, null, null, null, "'SOURCE'", "'WATCH'", 
			"'WHERE'", "'PUBLISH'", "'AND'", "'OR'", "'CONTAINS'"
		};
	}
	private static final String[] _LITERAL_NAMES = makeLiteralNames();
	private static String[] makeSymbolicNames() {
		return new String[] {
			null, "WORD", "DIGIT", "ADDRESS", "WS", "BOOLEAN_OPERATOR", "ARITHMETIC_SYMBOL", 
			"CONDITION_VALUE", "SOURCE", "WATCH", "WHERE", "PUBLISH", "AND", "OR", 
			"CONTAINS", "PUBLISH_VALUE", "WEBSOCKETS", "LOG_PUBLISHER"
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
		"\t\22\3\2\6\2\'\n\2\r\2\16\2(\3\3\6\3,\n\3\r\3\16\3-\3\4\3\4\3\4\3\4\7"+
		"\4\64\n\4\f\4\16\4\67\13\4\3\5\6\5:\n\5\r\5\16\5;\3\5\3\5\3\6\3\6\5\6"+
		"B\n\6\3\7\3\7\3\7\3\7\3\7\3\7\3\7\3\7\3\7\5\7M\n\7\3\b\3\b\3\b\5\bR\n"+
		"\b\3\t\3\t\3\t\3\t\3\t\3\t\3\t\3\n\3\n\3\n\3\n\3\n\3\n\3\13\3\13\3\13"+
		"\3\13\3\13\3\13\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\r\3\r\3\r\3\r\3\16\3"+
		"\16\3\16\3\17\3\17\3\17\3\17\3\17\3\17\3\17\3\17\3\17\3\20\3\20\5\20\u0081"+
		"\n\20\3\21\3\21\3\21\3\21\3\21\3\21\3\21\3\21\3\21\3\21\3\21\3\21\3\21"+
		"\3\21\3\21\3\21\3\21\3\21\3\21\3\21\3\21\3\21\3\21\3\21\3\21\3\21\3\21"+
		"\3\21\3\21\3\21\5\21\u00a1\n\21\3\22\3\22\3\22\3\22\3\22\3\22\3\22\3\22"+
		"\3\22\3\22\3\22\3\22\3\22\3\22\3\22\3\22\3\22\3\22\3\22\3\22\3\22\3\22"+
		"\3\22\3\22\3\22\3\22\3\22\3\22\3\22\3\22\3\22\3\22\3\22\3\22\3\22\3\22"+
		"\5\22\u00c7\n\22\2\2\23\3\3\5\4\7\5\t\6\13\7\r\b\17\t\21\n\23\13\25\f"+
		"\27\r\31\16\33\17\35\20\37\21!\22#\23\3\2\7\4\2C\\c|\3\2\62;\5\2\62;C"+
		"Hch\5\2\13\f\17\17\"\"\4\2>>@@\2\u00d7\2\3\3\2\2\2\2\5\3\2\2\2\2\7\3\2"+
		"\2\2\2\t\3\2\2\2\2\13\3\2\2\2\2\r\3\2\2\2\2\17\3\2\2\2\2\21\3\2\2\2\2"+
		"\23\3\2\2\2\2\25\3\2\2\2\2\27\3\2\2\2\2\31\3\2\2\2\2\33\3\2\2\2\2\35\3"+
		"\2\2\2\2\37\3\2\2\2\2!\3\2\2\2\2#\3\2\2\2\3&\3\2\2\2\5+\3\2\2\2\7/\3\2"+
		"\2\2\t9\3\2\2\2\13A\3\2\2\2\rL\3\2\2\2\17Q\3\2\2\2\21S\3\2\2\2\23Z\3\2"+
		"\2\2\25`\3\2\2\2\27f\3\2\2\2\31n\3\2\2\2\33r\3\2\2\2\35u\3\2\2\2\37\u0080"+
		"\3\2\2\2!\u00a0\3\2\2\2#\u00c6\3\2\2\2%\'\t\2\2\2&%\3\2\2\2\'(\3\2\2\2"+
		"(&\3\2\2\2()\3\2\2\2)\4\3\2\2\2*,\t\3\2\2+*\3\2\2\2,-\3\2\2\2-+\3\2\2"+
		"\2-.\3\2\2\2.\6\3\2\2\2/\60\7\62\2\2\60\61\7z\2\2\61\65\3\2\2\2\62\64"+
		"\t\4\2\2\63\62\3\2\2\2\64\67\3\2\2\2\65\63\3\2\2\2\65\66\3\2\2\2\66\b"+
		"\3\2\2\2\67\65\3\2\2\28:\t\5\2\298\3\2\2\2:;\3\2\2\2;9\3\2\2\2;<\3\2\2"+
		"\2<=\3\2\2\2=>\b\5\2\2>\n\3\2\2\2?B\5\r\7\2@B\5\35\17\2A?\3\2\2\2A@\3"+
		"\2\2\2B\f\3\2\2\2CD\7?\2\2DM\7?\2\2EF\7#\2\2FM\7?\2\2GM\t\6\2\2HI\7>\2"+
		"\2IM\7?\2\2JK\7@\2\2KM\7?\2\2LC\3\2\2\2LE\3\2\2\2LG\3\2\2\2LH\3\2\2\2"+
		"LJ\3\2\2\2M\16\3\2\2\2NR\5\3\2\2OR\5\5\3\2PR\5\7\4\2QN\3\2\2\2QO\3\2\2"+
		"\2QP\3\2\2\2R\20\3\2\2\2ST\7U\2\2TU\7Q\2\2UV\7W\2\2VW\7T\2\2WX\7E\2\2"+
		"XY\7G\2\2Y\22\3\2\2\2Z[\7Y\2\2[\\\7C\2\2\\]\7V\2\2]^\7E\2\2^_\7J\2\2_"+
		"\24\3\2\2\2`a\7Y\2\2ab\7J\2\2bc\7G\2\2cd\7T\2\2de\7G\2\2e\26\3\2\2\2f"+
		"g\7R\2\2gh\7W\2\2hi\7D\2\2ij\7N\2\2jk\7K\2\2kl\7U\2\2lm\7J\2\2m\30\3\2"+
		"\2\2no\7C\2\2op\7P\2\2pq\7F\2\2q\32\3\2\2\2rs\7Q\2\2st\7T\2\2t\34\3\2"+
		"\2\2uv\7E\2\2vw\7Q\2\2wx\7P\2\2xy\7V\2\2yz\7C\2\2z{\7K\2\2{|\7P\2\2|}"+
		"\7U\2\2}\36\3\2\2\2~\u0081\5!\21\2\177\u0081\5#\22\2\u0080~\3\2\2\2\u0080"+
		"\177\3\2\2\2\u0081 \3\2\2\2\u0082\u0083\7Y\2\2\u0083\u0084\7g\2\2\u0084"+
		"\u0085\7d\2\2\u0085\u0086\7U\2\2\u0086\u0087\7q\2\2\u0087\u0088\7e\2\2"+
		"\u0088\u0089\7m\2\2\u0089\u008a\7g\2\2\u008a\u008b\7v\2\2\u008b\u00a1"+
		"\7u\2\2\u008c\u008d\7y\2\2\u008d\u008e\7g\2\2\u008e\u008f\7d\2\2\u008f"+
		"\u0090\7U\2\2\u0090\u0091\7q\2\2\u0091\u0092\7e\2\2\u0092\u0093\7m\2\2"+
		"\u0093\u0094\7g\2\2\u0094\u0095\7v\2\2\u0095\u00a1\7u\2\2\u0096\u0097"+
		"\7y\2\2\u0097\u0098\7g\2\2\u0098\u0099\7d\2\2\u0099\u009a\7u\2\2\u009a"+
		"\u009b\7q\2\2\u009b\u009c\7e\2\2\u009c\u009d\7m\2\2\u009d\u009e\7g\2\2"+
		"\u009e\u009f\7v\2\2\u009f\u00a1\7u\2\2\u00a0\u0082\3\2\2\2\u00a0\u008c"+
		"\3\2\2\2\u00a0\u0096\3\2\2\2\u00a1\"\3\2\2\2\u00a2\u00a3\7N\2\2\u00a3"+
		"\u00a4\7q\2\2\u00a4\u00a5\7i\2\2\u00a5\u00a6\7R\2\2\u00a6\u00a7\7w\2\2"+
		"\u00a7\u00a8\7d\2\2\u00a8\u00a9\7n\2\2\u00a9\u00aa\7k\2\2\u00aa\u00ab"+
		"\7u\2\2\u00ab\u00ac\7j\2\2\u00ac\u00ad\7g\2\2\u00ad\u00c7\7t\2\2\u00ae"+
		"\u00af\7n\2\2\u00af\u00b0\7q\2\2\u00b0\u00b1\7i\2\2\u00b1\u00b2\7R\2\2"+
		"\u00b2\u00b3\7w\2\2\u00b3\u00b4\7d\2\2\u00b4\u00b5\7n\2\2\u00b5\u00b6"+
		"\7k\2\2\u00b6\u00b7\7u\2\2\u00b7\u00b8\7j\2\2\u00b8\u00b9\7g\2\2\u00b9"+
		"\u00c7\7t\2\2\u00ba\u00bb\7n\2\2\u00bb\u00bc\7q\2\2\u00bc\u00bd\7i\2\2"+
		"\u00bd\u00be\7r\2\2\u00be\u00bf\7w\2\2\u00bf\u00c0\7d\2\2\u00c0\u00c1"+
		"\7n\2\2\u00c1\u00c2\7k\2\2\u00c2\u00c3\7u\2\2\u00c3\u00c4\7j\2\2\u00c4"+
		"\u00c5\7g\2\2\u00c5\u00c7\7t\2\2\u00c6\u00a2\3\2\2\2\u00c6\u00ae\3\2\2"+
		"\2\u00c6\u00ba\3\2\2\2\u00c7$\3\2\2\2\r\2(-\65;ALQ\u0080\u00a0\u00c6\3"+
		"\b\2\2";
	public static final ATN _ATN =
		new ATNDeserializer().deserialize(_serializedATN.toCharArray());
	static {
		_decisionToDFA = new DFA[_ATN.getNumberOfDecisions()];
		for (int i = 0; i < _ATN.getNumberOfDecisions(); i++) {
			_decisionToDFA[i] = new DFA(_ATN.getDecisionState(i), i);
		}
	}
}