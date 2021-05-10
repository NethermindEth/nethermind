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
		ARITHMETIC_SYMBOL=1, SOURCE=2, WATCH=3, WHERE=4, PUBLISH=5, AND=6, OR=7, 
		CONTAINS=8, WEBSOCKETS=9, LOG_PUBLISHER=10, WORD=11, DIGIT=12, ADDRESS=13, 
		WS=14;
	public static String[] channelNames = {
		"DEFAULT_TOKEN_CHANNEL", "HIDDEN"
	};

	public static String[] modeNames = {
		"DEFAULT_MODE"
	};

	private static String[] makeRuleNames() {
		return new String[] {
			"ARITHMETIC_SYMBOL", "SOURCE", "WATCH", "WHERE", "PUBLISH", "AND", "OR", 
			"CONTAINS", "WEBSOCKETS", "LOG_PUBLISHER", "WORD", "DIGIT", "ADDRESS", 
			"WS"
		};
	}
	public static final String[] ruleNames = makeRuleNames();

	private static String[] makeLiteralNames() {
		return new String[] {
			null, null, "'SOURCE'", "'WATCH'", "'WHERE'", "'PUBLISH'", "'AND'", "'OR'", 
			"'CONTAINS'"
		};
	}
	private static final String[] _LITERAL_NAMES = makeLiteralNames();
	private static String[] makeSymbolicNames() {
		return new String[] {
			null, "ARITHMETIC_SYMBOL", "SOURCE", "WATCH", "WHERE", "PUBLISH", "AND", 
			"OR", "CONTAINS", "WEBSOCKETS", "LOG_PUBLISHER", "WORD", "DIGIT", "ADDRESS", 
			"WS"
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
		"\3\u608b\ua72a\u8133\ub9ed\u417c\u3be7\u7786\u5964\2\20\u00b5\b\1\4\2"+
		"\t\2\4\3\t\3\4\4\t\4\4\5\t\5\4\6\t\6\4\7\t\7\4\b\t\b\4\t\t\t\4\n\t\n\4"+
		"\13\t\13\4\f\t\f\4\r\t\r\4\16\t\16\4\17\t\17\3\2\3\2\3\2\3\2\3\2\3\2\3"+
		"\2\3\2\3\2\5\2)\n\2\3\3\3\3\3\3\3\3\3\3\3\3\3\3\3\4\3\4\3\4\3\4\3\4\3"+
		"\4\3\5\3\5\3\5\3\5\3\5\3\5\3\6\3\6\3\6\3\6\3\6\3\6\3\6\3\6\3\7\3\7\3\7"+
		"\3\7\3\b\3\b\3\b\3\t\3\t\3\t\3\t\3\t\3\t\3\t\3\t\3\t\3\n\3\n\3\n\3\n\3"+
		"\n\3\n\3\n\3\n\3\n\3\n\3\n\3\n\3\n\3\n\3\n\3\n\3\n\3\n\3\n\3\n\3\n\3\n"+
		"\3\n\3\n\3\n\3\n\3\n\3\n\3\n\3\n\5\nt\n\n\3\13\3\13\3\13\3\13\3\13\3\13"+
		"\3\13\3\13\3\13\3\13\3\13\3\13\3\13\3\13\3\13\3\13\3\13\3\13\3\13\3\13"+
		"\3\13\3\13\3\13\3\13\3\13\3\13\3\13\3\13\3\13\3\13\3\13\3\13\3\13\3\13"+
		"\3\13\3\13\5\13\u009a\n\13\3\f\6\f\u009d\n\f\r\f\16\f\u009e\3\r\6\r\u00a2"+
		"\n\r\r\r\16\r\u00a3\3\16\3\16\3\16\3\16\7\16\u00aa\n\16\f\16\16\16\u00ad"+
		"\13\16\3\17\6\17\u00b0\n\17\r\17\16\17\u00b1\3\17\3\17\2\2\20\3\3\5\4"+
		"\7\5\t\6\13\7\r\b\17\t\21\n\23\13\25\f\27\r\31\16\33\17\35\20\3\2\7\4"+
		"\2>>@@\4\2C\\c|\3\2\62;\5\2\62;CHch\5\2\13\f\17\17\"\"\2\u00c0\2\3\3\2"+
		"\2\2\2\5\3\2\2\2\2\7\3\2\2\2\2\t\3\2\2\2\2\13\3\2\2\2\2\r\3\2\2\2\2\17"+
		"\3\2\2\2\2\21\3\2\2\2\2\23\3\2\2\2\2\25\3\2\2\2\2\27\3\2\2\2\2\31\3\2"+
		"\2\2\2\33\3\2\2\2\2\35\3\2\2\2\3(\3\2\2\2\5*\3\2\2\2\7\61\3\2\2\2\t\67"+
		"\3\2\2\2\13=\3\2\2\2\rE\3\2\2\2\17I\3\2\2\2\21L\3\2\2\2\23s\3\2\2\2\25"+
		"\u0099\3\2\2\2\27\u009c\3\2\2\2\31\u00a1\3\2\2\2\33\u00a5\3\2\2\2\35\u00af"+
		"\3\2\2\2\37 \7?\2\2 )\7?\2\2!\"\7#\2\2\")\7?\2\2#)\t\2\2\2$%\7>\2\2%)"+
		"\7?\2\2&\'\7@\2\2\')\7?\2\2(\37\3\2\2\2(!\3\2\2\2(#\3\2\2\2($\3\2\2\2"+
		"(&\3\2\2\2)\4\3\2\2\2*+\7U\2\2+,\7Q\2\2,-\7W\2\2-.\7T\2\2./\7E\2\2/\60"+
		"\7G\2\2\60\6\3\2\2\2\61\62\7Y\2\2\62\63\7C\2\2\63\64\7V\2\2\64\65\7E\2"+
		"\2\65\66\7J\2\2\66\b\3\2\2\2\678\7Y\2\289\7J\2\29:\7G\2\2:;\7T\2\2;<\7"+
		"G\2\2<\n\3\2\2\2=>\7R\2\2>?\7W\2\2?@\7D\2\2@A\7N\2\2AB\7K\2\2BC\7U\2\2"+
		"CD\7J\2\2D\f\3\2\2\2EF\7C\2\2FG\7P\2\2GH\7F\2\2H\16\3\2\2\2IJ\7Q\2\2J"+
		"K\7T\2\2K\20\3\2\2\2LM\7E\2\2MN\7Q\2\2NO\7P\2\2OP\7V\2\2PQ\7C\2\2QR\7"+
		"K\2\2RS\7P\2\2ST\7U\2\2T\22\3\2\2\2UV\7Y\2\2VW\7g\2\2WX\7d\2\2XY\7U\2"+
		"\2YZ\7q\2\2Z[\7e\2\2[\\\7m\2\2\\]\7g\2\2]^\7v\2\2^t\7u\2\2_`\7y\2\2`a"+
		"\7g\2\2ab\7d\2\2bc\7U\2\2cd\7q\2\2de\7e\2\2ef\7m\2\2fg\7g\2\2gh\7v\2\2"+
		"ht\7u\2\2ij\7y\2\2jk\7g\2\2kl\7d\2\2lm\7u\2\2mn\7q\2\2no\7e\2\2op\7m\2"+
		"\2pq\7g\2\2qr\7v\2\2rt\7u\2\2sU\3\2\2\2s_\3\2\2\2si\3\2\2\2t\24\3\2\2"+
		"\2uv\7N\2\2vw\7q\2\2wx\7i\2\2xy\7R\2\2yz\7w\2\2z{\7d\2\2{|\7n\2\2|}\7"+
		"k\2\2}~\7u\2\2~\177\7j\2\2\177\u0080\7g\2\2\u0080\u009a\7t\2\2\u0081\u0082"+
		"\7n\2\2\u0082\u0083\7q\2\2\u0083\u0084\7i\2\2\u0084\u0085\7R\2\2\u0085"+
		"\u0086\7w\2\2\u0086\u0087\7d\2\2\u0087\u0088\7n\2\2\u0088\u0089\7k\2\2"+
		"\u0089\u008a\7u\2\2\u008a\u008b\7j\2\2\u008b\u008c\7g\2\2\u008c\u009a"+
		"\7t\2\2\u008d\u008e\7n\2\2\u008e\u008f\7q\2\2\u008f\u0090\7i\2\2\u0090"+
		"\u0091\7r\2\2\u0091\u0092\7w\2\2\u0092\u0093\7d\2\2\u0093\u0094\7n\2\2"+
		"\u0094\u0095\7k\2\2\u0095\u0096\7u\2\2\u0096\u0097\7j\2\2\u0097\u0098"+
		"\7g\2\2\u0098\u009a\7t\2\2\u0099u\3\2\2\2\u0099\u0081\3\2\2\2\u0099\u008d"+
		"\3\2\2\2\u009a\26\3\2\2\2\u009b\u009d\t\3\2\2\u009c\u009b\3\2\2\2\u009d"+
		"\u009e\3\2\2\2\u009e\u009c\3\2\2\2\u009e\u009f\3\2\2\2\u009f\30\3\2\2"+
		"\2\u00a0\u00a2\t\4\2\2\u00a1\u00a0\3\2\2\2\u00a2\u00a3\3\2\2\2\u00a3\u00a1"+
		"\3\2\2\2\u00a3\u00a4\3\2\2\2\u00a4\32\3\2\2\2\u00a5\u00a6\7\62\2\2\u00a6"+
		"\u00a7\7z\2\2\u00a7\u00ab\3\2\2\2\u00a8\u00aa\t\5\2\2\u00a9\u00a8\3\2"+
		"\2\2\u00aa\u00ad\3\2\2\2\u00ab\u00a9\3\2\2\2\u00ab\u00ac\3\2\2\2\u00ac"+
		"\34\3\2\2\2\u00ad\u00ab\3\2\2\2\u00ae\u00b0\t\6\2\2\u00af\u00ae\3\2\2"+
		"\2\u00b0\u00b1\3\2\2\2\u00b1\u00af\3\2\2\2\u00b1\u00b2\3\2\2\2\u00b2\u00b3"+
		"\3\2\2\2\u00b3\u00b4\b\17\2\2\u00b4\36\3\2\2\2\n\2(s\u0099\u009e\u00a3"+
		"\u00ab\u00b1\3\b\2\2";
	public static final ATN _ATN =
		new ATNDeserializer().deserialize(_serializedATN.toCharArray());
	static {
		_decisionToDFA = new DFA[_ATN.getNumberOfDecisions()];
		for (int i = 0; i < _ATN.getNumberOfDecisions(); i++) {
			_decisionToDFA[i] = new DFA(_ATN.getDecisionState(i), i);
		}
	}
}