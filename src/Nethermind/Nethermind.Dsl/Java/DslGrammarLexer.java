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
		AND=7, OR=8, CONTAINS=9, WEBSOCKETS=10, LOG_PUBLISHER=11, WORD=12, DIGIT=13, 
		ADDRESS=14, WS=15;
	public static String[] channelNames = {
		"DEFAULT_TOKEN_CHANNEL", "HIDDEN"
	};

	public static String[] modeNames = {
		"DEFAULT_MODE"
	};

	private static String[] makeRuleNames() {
		return new String[] {
			"BOOLEAN_OPERATOR", "ARITHMETIC_SYMBOL", "SOURCE", "WATCH", "WHERE", 
			"PUBLISH", "AND", "OR", "CONTAINS", "WEBSOCKETS", "LOG_PUBLISHER", "WORD", 
			"DIGIT", "ADDRESS", "WS"
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
			"PUBLISH", "AND", "OR", "CONTAINS", "WEBSOCKETS", "LOG_PUBLISHER", "WORD", 
			"DIGIT", "ADDRESS", "WS"
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
		"\3\u608b\ua72a\u8133\ub9ed\u417c\u3be7\u7786\u5964\2\21\u00bb\b\1\4\2"+
		"\t\2\4\3\t\3\4\4\t\4\4\5\t\5\4\6\t\6\4\7\t\7\4\b\t\b\4\t\t\t\4\n\t\n\4"+
		"\13\t\13\4\f\t\f\4\r\t\r\4\16\t\16\4\17\t\17\4\20\t\20\3\2\3\2\5\2$\n"+
		"\2\3\3\3\3\3\3\3\3\3\3\3\3\3\3\3\3\3\3\5\3/\n\3\3\4\3\4\3\4\3\4\3\4\3"+
		"\4\3\4\3\5\3\5\3\5\3\5\3\5\3\5\3\6\3\6\3\6\3\6\3\6\3\6\3\7\3\7\3\7\3\7"+
		"\3\7\3\7\3\7\3\7\3\b\3\b\3\b\3\b\3\t\3\t\3\t\3\n\3\n\3\n\3\n\3\n\3\n\3"+
		"\n\3\n\3\n\3\13\3\13\3\13\3\13\3\13\3\13\3\13\3\13\3\13\3\13\3\13\3\13"+
		"\3\13\3\13\3\13\3\13\3\13\3\13\3\13\3\13\3\13\3\13\3\13\3\13\3\13\3\13"+
		"\3\13\3\13\3\13\3\13\5\13z\n\13\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3"+
		"\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f"+
		"\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\3\f\5\f\u00a0\n\f\3\r\6\r\u00a3\n\r\r"+
		"\r\16\r\u00a4\3\16\6\16\u00a8\n\16\r\16\16\16\u00a9\3\17\3\17\3\17\3\17"+
		"\7\17\u00b0\n\17\f\17\16\17\u00b3\13\17\3\20\6\20\u00b6\n\20\r\20\16\20"+
		"\u00b7\3\20\3\20\2\2\21\3\3\5\4\7\5\t\6\13\7\r\b\17\t\21\n\23\13\25\f"+
		"\27\r\31\16\33\17\35\20\37\21\3\2\7\4\2>>@@\4\2C\\c|\3\2\62;\5\2\62;C"+
		"Hch\5\2\13\f\17\17\"\"\2\u00c7\2\3\3\2\2\2\2\5\3\2\2\2\2\7\3\2\2\2\2\t"+
		"\3\2\2\2\2\13\3\2\2\2\2\r\3\2\2\2\2\17\3\2\2\2\2\21\3\2\2\2\2\23\3\2\2"+
		"\2\2\25\3\2\2\2\2\27\3\2\2\2\2\31\3\2\2\2\2\33\3\2\2\2\2\35\3\2\2\2\2"+
		"\37\3\2\2\2\3#\3\2\2\2\5.\3\2\2\2\7\60\3\2\2\2\t\67\3\2\2\2\13=\3\2\2"+
		"\2\rC\3\2\2\2\17K\3\2\2\2\21O\3\2\2\2\23R\3\2\2\2\25y\3\2\2\2\27\u009f"+
		"\3\2\2\2\31\u00a2\3\2\2\2\33\u00a7\3\2\2\2\35\u00ab\3\2\2\2\37\u00b5\3"+
		"\2\2\2!$\5\5\3\2\"$\5\23\n\2#!\3\2\2\2#\"\3\2\2\2$\4\3\2\2\2%&\7?\2\2"+
		"&/\7?\2\2\'(\7#\2\2(/\7?\2\2)/\t\2\2\2*+\7>\2\2+/\7?\2\2,-\7@\2\2-/\7"+
		"?\2\2.%\3\2\2\2.\'\3\2\2\2.)\3\2\2\2.*\3\2\2\2.,\3\2\2\2/\6\3\2\2\2\60"+
		"\61\7U\2\2\61\62\7Q\2\2\62\63\7W\2\2\63\64\7T\2\2\64\65\7E\2\2\65\66\7"+
		"G\2\2\66\b\3\2\2\2\678\7Y\2\289\7C\2\29:\7V\2\2:;\7E\2\2;<\7J\2\2<\n\3"+
		"\2\2\2=>\7Y\2\2>?\7J\2\2?@\7G\2\2@A\7T\2\2AB\7G\2\2B\f\3\2\2\2CD\7R\2"+
		"\2DE\7W\2\2EF\7D\2\2FG\7N\2\2GH\7K\2\2HI\7U\2\2IJ\7J\2\2J\16\3\2\2\2K"+
		"L\7C\2\2LM\7P\2\2MN\7F\2\2N\20\3\2\2\2OP\7Q\2\2PQ\7T\2\2Q\22\3\2\2\2R"+
		"S\7E\2\2ST\7Q\2\2TU\7P\2\2UV\7V\2\2VW\7C\2\2WX\7K\2\2XY\7P\2\2YZ\7U\2"+
		"\2Z\24\3\2\2\2[\\\7Y\2\2\\]\7g\2\2]^\7d\2\2^_\7U\2\2_`\7q\2\2`a\7e\2\2"+
		"ab\7m\2\2bc\7g\2\2cd\7v\2\2dz\7u\2\2ef\7y\2\2fg\7g\2\2gh\7d\2\2hi\7U\2"+
		"\2ij\7q\2\2jk\7e\2\2kl\7m\2\2lm\7g\2\2mn\7v\2\2nz\7u\2\2op\7y\2\2pq\7"+
		"g\2\2qr\7d\2\2rs\7u\2\2st\7q\2\2tu\7e\2\2uv\7m\2\2vw\7g\2\2wx\7v\2\2x"+
		"z\7u\2\2y[\3\2\2\2ye\3\2\2\2yo\3\2\2\2z\26\3\2\2\2{|\7N\2\2|}\7q\2\2}"+
		"~\7i\2\2~\177\7R\2\2\177\u0080\7w\2\2\u0080\u0081\7d\2\2\u0081\u0082\7"+
		"n\2\2\u0082\u0083\7k\2\2\u0083\u0084\7u\2\2\u0084\u0085\7j\2\2\u0085\u0086"+
		"\7g\2\2\u0086\u00a0\7t\2\2\u0087\u0088\7n\2\2\u0088\u0089\7q\2\2\u0089"+
		"\u008a\7i\2\2\u008a\u008b\7R\2\2\u008b\u008c\7w\2\2\u008c\u008d\7d\2\2"+
		"\u008d\u008e\7n\2\2\u008e\u008f\7k\2\2\u008f\u0090\7u\2\2\u0090\u0091"+
		"\7j\2\2\u0091\u0092\7g\2\2\u0092\u00a0\7t\2\2\u0093\u0094\7n\2\2\u0094"+
		"\u0095\7q\2\2\u0095\u0096\7i\2\2\u0096\u0097\7r\2\2\u0097\u0098\7w\2\2"+
		"\u0098\u0099\7d\2\2\u0099\u009a\7n\2\2\u009a\u009b\7k\2\2\u009b\u009c"+
		"\7u\2\2\u009c\u009d\7j\2\2\u009d\u009e\7g\2\2\u009e\u00a0\7t\2\2\u009f"+
		"{\3\2\2\2\u009f\u0087\3\2\2\2\u009f\u0093\3\2\2\2\u00a0\30\3\2\2\2\u00a1"+
		"\u00a3\t\3\2\2\u00a2\u00a1\3\2\2\2\u00a3\u00a4\3\2\2\2\u00a4\u00a2\3\2"+
		"\2\2\u00a4\u00a5\3\2\2\2\u00a5\32\3\2\2\2\u00a6\u00a8\t\4\2\2\u00a7\u00a6"+
		"\3\2\2\2\u00a8\u00a9\3\2\2\2\u00a9\u00a7\3\2\2\2\u00a9\u00aa\3\2\2\2\u00aa"+
		"\34\3\2\2\2\u00ab\u00ac\7\62\2\2\u00ac\u00ad\7z\2\2\u00ad\u00b1\3\2\2"+
		"\2\u00ae\u00b0\t\5\2\2\u00af\u00ae\3\2\2\2\u00b0\u00b3\3\2\2\2\u00b1\u00af"+
		"\3\2\2\2\u00b1\u00b2\3\2\2\2\u00b2\36\3\2\2\2\u00b3\u00b1\3\2\2\2\u00b4"+
		"\u00b6\t\6\2\2\u00b5\u00b4\3\2\2\2\u00b6\u00b7\3\2\2\2\u00b7\u00b5\3\2"+
		"\2\2\u00b7\u00b8\3\2\2\2\u00b8\u00b9\3\2\2\2\u00b9\u00ba\b\20\2\2\u00ba"+
		" \3\2\2\2\13\2#.y\u009f\u00a4\u00a9\u00b1\u00b7\3\b\2\2";
	public static final ATN _ATN =
		new ATNDeserializer().deserialize(_serializedATN.toCharArray());
	static {
		_decisionToDFA = new DFA[_ATN.getNumberOfDecisions()];
		for (int i = 0; i < _ATN.getNumberOfDecisions(); i++) {
			_decisionToDFA[i] = new DFA(_ATN.getDecisionState(i), i);
		}
	}
}