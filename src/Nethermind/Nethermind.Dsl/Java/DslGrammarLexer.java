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
		SOURCE=1, WATCH=2, WHERE=3, PUBLISH=4, AND=5, OR=6, CONTAINS=7, BOOLEAN_OPERATOR=8, 
		ARITHMETIC_SYMBOL=9, PUBLISH_VALUE=10, WEBSOCKETS=11, LOG_PUBLISHER=12, 
		WORD=13, DIGIT=14, ADDRESS=15, WS=16;
	public static String[] channelNames = {
		"DEFAULT_TOKEN_CHANNEL", "HIDDEN"
	};

	public static String[] modeNames = {
		"DEFAULT_MODE"
	};

	private static String[] makeRuleNames() {
		return new String[] {
			"SOURCE", "WATCH", "WHERE", "PUBLISH", "AND", "OR", "CONTAINS", "BOOLEAN_OPERATOR", 
			"ARITHMETIC_SYMBOL", "PUBLISH_VALUE", "WEBSOCKETS", "LOG_PUBLISHER", 
			"WORD", "DIGIT", "ADDRESS", "WS"
		};
	}
	public static final String[] ruleNames = makeRuleNames();

	private static String[] makeLiteralNames() {
		return new String[] {
			null, "'SOURCE'", "'WATCH'", "'WHERE'", "'PUBLISH'", "'AND'", "'OR'", 
			"'CONTAINS'"
		};
	}
	private static final String[] _LITERAL_NAMES = makeLiteralNames();
	private static String[] makeSymbolicNames() {
		return new String[] {
			null, "SOURCE", "WATCH", "WHERE", "PUBLISH", "AND", "OR", "CONTAINS", 
			"BOOLEAN_OPERATOR", "ARITHMETIC_SYMBOL", "PUBLISH_VALUE", "WEBSOCKETS", 
			"LOG_PUBLISHER", "WORD", "DIGIT", "ADDRESS", "WS"
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
		"\3\u608b\ua72a\u8133\ub9ed\u417c\u3be7\u7786\u5964\2\22\u00c1\b\1\4\2"+
		"\t\2\4\3\t\3\4\4\t\4\4\5\t\5\4\6\t\6\4\7\t\7\4\b\t\b\4\t\t\t\4\n\t\n\4"+
		"\13\t\13\4\f\t\f\4\r\t\r\4\16\t\16\4\17\t\17\4\20\t\20\4\21\t\21\3\2\3"+
		"\2\3\2\3\2\3\2\3\2\3\2\3\3\3\3\3\3\3\3\3\3\3\3\3\4\3\4\3\4\3\4\3\4\3\4"+
		"\3\5\3\5\3\5\3\5\3\5\3\5\3\5\3\5\3\6\3\6\3\6\3\6\3\7\3\7\3\7\3\b\3\b\3"+
		"\b\3\b\3\b\3\b\3\b\3\b\3\b\3\t\3\t\5\tQ\n\t\3\n\3\n\3\n\3\n\3\n\3\n\3"+
		"\n\3\n\3\n\5\n\\\n\n\3\13\3\13\5\13`\n\13\3\f\3\f\3\f\3\f\3\f\3\f\3\f"+
		"\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3"+
		"\f\3\f\3\f\3\f\3\f\3\f\5\f\u0080\n\f\3\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r\3"+
		"\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r"+
		"\3\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r\3\r\5\r\u00a6\n\r\3\16\6\16\u00a9"+
		"\n\16\r\16\16\16\u00aa\3\17\6\17\u00ae\n\17\r\17\16\17\u00af\3\20\3\20"+
		"\3\20\3\20\7\20\u00b6\n\20\f\20\16\20\u00b9\13\20\3\21\6\21\u00bc\n\21"+
		"\r\21\16\21\u00bd\3\21\3\21\2\2\22\3\3\5\4\7\5\t\6\13\7\r\b\17\t\21\n"+
		"\23\13\25\f\27\r\31\16\33\17\35\20\37\21!\22\3\2\7\4\2>>@@\4\2C\\c|\3"+
		"\2\62;\5\2\62;CHch\5\2\13\f\17\17\"\"\2\u00ce\2\3\3\2\2\2\2\5\3\2\2\2"+
		"\2\7\3\2\2\2\2\t\3\2\2\2\2\13\3\2\2\2\2\r\3\2\2\2\2\17\3\2\2\2\2\21\3"+
		"\2\2\2\2\23\3\2\2\2\2\25\3\2\2\2\2\27\3\2\2\2\2\31\3\2\2\2\2\33\3\2\2"+
		"\2\2\35\3\2\2\2\2\37\3\2\2\2\2!\3\2\2\2\3#\3\2\2\2\5*\3\2\2\2\7\60\3\2"+
		"\2\2\t\66\3\2\2\2\13>\3\2\2\2\rB\3\2\2\2\17E\3\2\2\2\21P\3\2\2\2\23[\3"+
		"\2\2\2\25_\3\2\2\2\27\177\3\2\2\2\31\u00a5\3\2\2\2\33\u00a8\3\2\2\2\35"+
		"\u00ad\3\2\2\2\37\u00b1\3\2\2\2!\u00bb\3\2\2\2#$\7U\2\2$%\7Q\2\2%&\7W"+
		"\2\2&\'\7T\2\2\'(\7E\2\2()\7G\2\2)\4\3\2\2\2*+\7Y\2\2+,\7C\2\2,-\7V\2"+
		"\2-.\7E\2\2./\7J\2\2/\6\3\2\2\2\60\61\7Y\2\2\61\62\7J\2\2\62\63\7G\2\2"+
		"\63\64\7T\2\2\64\65\7G\2\2\65\b\3\2\2\2\66\67\7R\2\2\678\7W\2\289\7D\2"+
		"\29:\7N\2\2:;\7K\2\2;<\7U\2\2<=\7J\2\2=\n\3\2\2\2>?\7C\2\2?@\7P\2\2@A"+
		"\7F\2\2A\f\3\2\2\2BC\7Q\2\2CD\7T\2\2D\16\3\2\2\2EF\7E\2\2FG\7Q\2\2GH\7"+
		"P\2\2HI\7V\2\2IJ\7C\2\2JK\7K\2\2KL\7P\2\2LM\7U\2\2M\20\3\2\2\2NQ\5\23"+
		"\n\2OQ\5\17\b\2PN\3\2\2\2PO\3\2\2\2Q\22\3\2\2\2RS\7?\2\2S\\\7?\2\2TU\7"+
		"#\2\2U\\\7?\2\2V\\\t\2\2\2WX\7>\2\2X\\\7?\2\2YZ\7@\2\2Z\\\7?\2\2[R\3\2"+
		"\2\2[T\3\2\2\2[V\3\2\2\2[W\3\2\2\2[Y\3\2\2\2\\\24\3\2\2\2]`\5\27\f\2^"+
		"`\5\31\r\2_]\3\2\2\2_^\3\2\2\2`\26\3\2\2\2ab\7Y\2\2bc\7g\2\2cd\7d\2\2"+
		"de\7U\2\2ef\7q\2\2fg\7e\2\2gh\7m\2\2hi\7g\2\2ij\7v\2\2j\u0080\7u\2\2k"+
		"l\7y\2\2lm\7g\2\2mn\7d\2\2no\7U\2\2op\7q\2\2pq\7e\2\2qr\7m\2\2rs\7g\2"+
		"\2st\7v\2\2t\u0080\7u\2\2uv\7y\2\2vw\7g\2\2wx\7d\2\2xy\7u\2\2yz\7q\2\2"+
		"z{\7e\2\2{|\7m\2\2|}\7g\2\2}~\7v\2\2~\u0080\7u\2\2\177a\3\2\2\2\177k\3"+
		"\2\2\2\177u\3\2\2\2\u0080\30\3\2\2\2\u0081\u0082\7N\2\2\u0082\u0083\7"+
		"q\2\2\u0083\u0084\7i\2\2\u0084\u0085\7R\2\2\u0085\u0086\7w\2\2\u0086\u0087"+
		"\7d\2\2\u0087\u0088\7n\2\2\u0088\u0089\7k\2\2\u0089\u008a\7u\2\2\u008a"+
		"\u008b\7j\2\2\u008b\u008c\7g\2\2\u008c\u00a6\7t\2\2\u008d\u008e\7n\2\2"+
		"\u008e\u008f\7q\2\2\u008f\u0090\7i\2\2\u0090\u0091\7R\2\2\u0091\u0092"+
		"\7w\2\2\u0092\u0093\7d\2\2\u0093\u0094\7n\2\2\u0094\u0095\7k\2\2\u0095"+
		"\u0096\7u\2\2\u0096\u0097\7j\2\2\u0097\u0098\7g\2\2\u0098\u00a6\7t\2\2"+
		"\u0099\u009a\7n\2\2\u009a\u009b\7q\2\2\u009b\u009c\7i\2\2\u009c\u009d"+
		"\7r\2\2\u009d\u009e\7w\2\2\u009e\u009f\7d\2\2\u009f\u00a0\7n\2\2\u00a0"+
		"\u00a1\7k\2\2\u00a1\u00a2\7u\2\2\u00a2\u00a3\7j\2\2\u00a3\u00a4\7g\2\2"+
		"\u00a4\u00a6\7t\2\2\u00a5\u0081\3\2\2\2\u00a5\u008d\3\2\2\2\u00a5\u0099"+
		"\3\2\2\2\u00a6\32\3\2\2\2\u00a7\u00a9\t\3\2\2\u00a8\u00a7\3\2\2\2\u00a9"+
		"\u00aa\3\2\2\2\u00aa\u00a8\3\2\2\2\u00aa\u00ab\3\2\2\2\u00ab\34\3\2\2"+
		"\2\u00ac\u00ae\t\4\2\2\u00ad\u00ac\3\2\2\2\u00ae\u00af\3\2\2\2\u00af\u00ad"+
		"\3\2\2\2\u00af\u00b0\3\2\2\2\u00b0\36\3\2\2\2\u00b1\u00b2\7\62\2\2\u00b2"+
		"\u00b3\7z\2\2\u00b3\u00b7\3\2\2\2\u00b4\u00b6\t\5\2\2\u00b5\u00b4\3\2"+
		"\2\2\u00b6\u00b9\3\2\2\2\u00b7\u00b5\3\2\2\2\u00b7\u00b8\3\2\2\2\u00b8"+
		" \3\2\2\2\u00b9\u00b7\3\2\2\2\u00ba\u00bc\t\6\2\2\u00bb\u00ba\3\2\2\2"+
		"\u00bc\u00bd\3\2\2\2\u00bd\u00bb\3\2\2\2\u00bd\u00be\3\2\2\2\u00be\u00bf"+
		"\3\2\2\2\u00bf\u00c0\b\21\2\2\u00c0\"\3\2\2\2\f\2P[_\177\u00a5\u00aa\u00af"+
		"\u00b7\u00bd\3\b\2\2";
	public static final ATN _ATN =
		new ATNDeserializer().deserialize(_serializedATN.toCharArray());
	static {
		_decisionToDFA = new DFA[_ATN.getNumberOfDecisions()];
		for (int i = 0; i < _ATN.getNumberOfDecisions(); i++) {
			_decisionToDFA[i] = new DFA(_ATN.getDecisionState(i), i);
		}
	}
}