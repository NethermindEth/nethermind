// Generated from /home/jhweintraub/nethermind_repos/nethermind/src/Nethermind/Nethermind.Dsl/Grammar/DslGrammar.g4 by ANTLR 4.8
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
		TELEGRAM=14, DISCORD=15, SLACK=16, WORD=17, DIGIT=18, BYTECODE=19, ADDRESS=20, 
		URL=21, WS=22;
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
			"TELEGRAM", "DISCORD", "SLACK", "WORD", "DIGIT", "BYTECODE", "ADDRESS", 
			"URL", "WS"
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
			"TELEGRAM", "DISCORD", "SLACK", "WORD", "DIGIT", "BYTECODE", "ADDRESS", 
			"URL", "WS"
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
		"\3\u608b\ua72a\u8133\ub9ed\u417c\u3be7\u7786\u5964\2\30\u00fa\b\1\4\2"+
		"\t\2\4\3\t\3\4\4\t\4\4\5\t\5\4\6\t\6\4\7\t\7\4\b\t\b\4\t\t\t\4\n\t\n\4"+
		"\13\t\13\4\f\t\f\4\r\t\r\4\16\t\16\4\17\t\17\4\20\t\20\4\21\t\21\4\22"+
		"\t\22\4\23\t\23\4\24\t\24\4\25\t\25\4\26\t\26\4\27\t\27\3\2\3\2\5\2\62"+
		"\n\2\3\3\3\3\3\3\3\3\3\3\3\3\3\3\3\3\3\3\3\3\3\3\5\3?\n\3\3\4\3\4\3\4"+
		"\3\4\3\4\3\4\3\4\3\5\3\5\3\5\3\5\3\5\3\5\3\6\3\6\3\6\3\6\3\6\3\6\3\7\3"+
		"\7\3\7\3\7\3\7\3\7\3\7\3\7\3\b\3\b\3\b\3\b\3\t\3\t\3\t\3\n\3\n\3\n\3\n"+
		"\3\n\3\n\3\n\3\n\3\n\3\13\3\13\3\13\3\f\3\f\3\f\3\f\3\r\3\r\3\r\3\r\5"+
		"\rw\n\r\3\16\3\16\3\16\3\16\3\16\3\16\3\16\3\16\3\16\3\16\3\16\3\16\3"+
		"\16\3\16\3\16\3\16\3\16\3\16\3\16\3\16\3\16\3\16\3\16\3\16\3\16\3\16\3"+
		"\16\3\16\3\16\3\16\3\16\3\16\3\16\3\16\3\16\3\16\3\16\3\16\3\16\3\16\5"+
		"\16\u00a1\n\16\3\17\3\17\3\17\3\17\3\17\3\17\3\17\3\17\3\17\3\17\3\17"+
		"\3\17\3\17\3\17\3\17\3\17\5\17\u00b3\n\17\3\20\3\20\3\20\3\20\3\20\3\20"+
		"\3\20\3\20\3\20\3\20\3\20\3\20\3\20\3\20\5\20\u00c3\n\20\3\21\3\21\3\21"+
		"\3\21\3\21\3\21\3\21\3\21\3\21\3\21\5\21\u00cf\n\21\3\22\6\22\u00d2\n"+
		"\22\r\22\16\22\u00d3\3\23\6\23\u00d7\n\23\r\23\16\23\u00d8\3\24\6\24\u00dc"+
		"\n\24\r\24\16\24\u00dd\3\25\3\25\3\25\3\25\7\25\u00e4\n\25\f\25\16\25"+
		"\u00e7\13\25\3\26\3\26\3\26\3\26\3\26\3\26\3\26\6\26\u00f0\n\26\r\26\16"+
		"\26\u00f1\3\27\6\27\u00f5\n\27\r\27\16\27\u00f6\3\27\3\27\3\u00f1\2\30"+
		"\3\3\5\4\7\5\t\6\13\7\r\b\17\t\21\n\23\13\25\f\27\r\31\16\33\17\35\20"+
		"\37\21!\22#\23%\24\'\25)\26+\27-\30\3\2\7\4\2>>@@\4\2C\\c|\3\2\62;\5\2"+
		"\62;CHch\5\2\13\f\17\17\"\"\2\u010f\2\3\3\2\2\2\2\5\3\2\2\2\2\7\3\2\2"+
		"\2\2\t\3\2\2\2\2\13\3\2\2\2\2\r\3\2\2\2\2\17\3\2\2\2\2\21\3\2\2\2\2\23"+
		"\3\2\2\2\2\25\3\2\2\2\2\27\3\2\2\2\2\31\3\2\2\2\2\33\3\2\2\2\2\35\3\2"+
		"\2\2\2\37\3\2\2\2\2!\3\2\2\2\2#\3\2\2\2\2%\3\2\2\2\2\'\3\2\2\2\2)\3\2"+
		"\2\2\2+\3\2\2\2\2-\3\2\2\2\3\61\3\2\2\2\5>\3\2\2\2\7@\3\2\2\2\tG\3\2\2"+
		"\2\13M\3\2\2\2\rS\3\2\2\2\17[\3\2\2\2\21_\3\2\2\2\23b\3\2\2\2\25k\3\2"+
		"\2\2\27n\3\2\2\2\31v\3\2\2\2\33\u00a0\3\2\2\2\35\u00b2\3\2\2\2\37\u00c2"+
		"\3\2\2\2!\u00ce\3\2\2\2#\u00d1\3\2\2\2%\u00d6\3\2\2\2\'\u00db\3\2\2\2"+
		")\u00df\3\2\2\2+\u00e8\3\2\2\2-\u00f4\3\2\2\2/\62\5\5\3\2\60\62\5\23\n"+
		"\2\61/\3\2\2\2\61\60\3\2\2\2\62\4\3\2\2\2\63\64\7?\2\2\64?\7?\2\2\65\66"+
		"\7#\2\2\66?\7?\2\2\67?\t\2\2\289\7>\2\29?\7?\2\2:;\7@\2\2;?\7?\2\2<?\5"+
		"\25\13\2=?\5\27\f\2>\63\3\2\2\2>\65\3\2\2\2>\67\3\2\2\2>8\3\2\2\2>:\3"+
		"\2\2\2><\3\2\2\2>=\3\2\2\2?\6\3\2\2\2@A\7U\2\2AB\7Q\2\2BC\7W\2\2CD\7T"+
		"\2\2DE\7E\2\2EF\7G\2\2F\b\3\2\2\2GH\7Y\2\2HI\7C\2\2IJ\7V\2\2JK\7E\2\2"+
		"KL\7J\2\2L\n\3\2\2\2MN\7Y\2\2NO\7J\2\2OP\7G\2\2PQ\7T\2\2QR\7G\2\2R\f\3"+
		"\2\2\2ST\7R\2\2TU\7W\2\2UV\7D\2\2VW\7N\2\2WX\7K\2\2XY\7U\2\2YZ\7J\2\2"+
		"Z\16\3\2\2\2[\\\7C\2\2\\]\7P\2\2]^\7F\2\2^\20\3\2\2\2_`\7Q\2\2`a\7T\2"+
		"\2a\22\3\2\2\2bc\7E\2\2cd\7Q\2\2de\7P\2\2ef\7V\2\2fg\7C\2\2gh\7K\2\2h"+
		"i\7P\2\2ij\7U\2\2j\24\3\2\2\2kl\7K\2\2lm\7U\2\2m\26\3\2\2\2no\7P\2\2o"+
		"p\7Q\2\2pq\7V\2\2q\30\3\2\2\2rw\5\33\16\2sw\5\35\17\2tw\5\37\20\2uw\5"+
		"!\21\2vr\3\2\2\2vs\3\2\2\2vt\3\2\2\2vu\3\2\2\2w\32\3\2\2\2xy\7Y\2\2yz"+
		"\7g\2\2z{\7d\2\2{|\7U\2\2|}\7q\2\2}~\7e\2\2~\177\7m\2\2\177\u0080\7g\2"+
		"\2\u0080\u0081\7v\2\2\u0081\u00a1\7u\2\2\u0082\u0083\7y\2\2\u0083\u0084"+
		"\7g\2\2\u0084\u0085\7d\2\2\u0085\u0086\7U\2\2\u0086\u0087\7q\2\2\u0087"+
		"\u0088\7e\2\2\u0088\u0089\7m\2\2\u0089\u008a\7g\2\2\u008a\u008b\7v\2\2"+
		"\u008b\u00a1\7u\2\2\u008c\u008d\7y\2\2\u008d\u008e\7g\2\2\u008e\u008f"+
		"\7d\2\2\u008f\u0090\7u\2\2\u0090\u0091\7q\2\2\u0091\u0092\7e\2\2\u0092"+
		"\u0093\7m\2\2\u0093\u0094\7g\2\2\u0094\u0095\7v\2\2\u0095\u00a1\7u\2\2"+
		"\u0096\u0097\7Y\2\2\u0097\u0098\7g\2\2\u0098\u0099\7d\2\2\u0099\u009a"+
		"\7u\2\2\u009a\u009b\7q\2\2\u009b\u009c\7e\2\2\u009c\u009d\7m\2\2\u009d"+
		"\u009e\7g\2\2\u009e\u009f\7v\2\2\u009f\u00a1\7u\2\2\u00a0x\3\2\2\2\u00a0"+
		"\u0082\3\2\2\2\u00a0\u008c\3\2\2\2\u00a0\u0096\3\2\2\2\u00a1\34\3\2\2"+
		"\2\u00a2\u00a3\7V\2\2\u00a3\u00a4\7g\2\2\u00a4\u00a5\7n\2\2\u00a5\u00a6"+
		"\7g\2\2\u00a6\u00a7\7i\2\2\u00a7\u00a8\7t\2\2\u00a8\u00a9\7c\2\2\u00a9"+
		"\u00b3\7o\2\2\u00aa\u00ab\7v\2\2\u00ab\u00ac\7g\2\2\u00ac\u00ad\7n\2\2"+
		"\u00ad\u00ae\7g\2\2\u00ae\u00af\7i\2\2\u00af\u00b0\7t\2\2\u00b0\u00b1"+
		"\7c\2\2\u00b1\u00b3\7o\2\2\u00b2\u00a2\3\2\2\2\u00b2\u00aa\3\2\2\2\u00b3"+
		"\36\3\2\2\2\u00b4\u00b5\7F\2\2\u00b5\u00b6\7k\2\2\u00b6\u00b7\7u\2\2\u00b7"+
		"\u00b8\7e\2\2\u00b8\u00b9\7q\2\2\u00b9\u00ba\7t\2\2\u00ba\u00c3\7f\2\2"+
		"\u00bb\u00bc\7f\2\2\u00bc\u00bd\7k\2\2\u00bd\u00be\7u\2\2\u00be\u00bf"+
		"\7e\2\2\u00bf\u00c0\7q\2\2\u00c0\u00c1\7t\2\2\u00c1\u00c3\7f\2\2\u00c2"+
		"\u00b4\3\2\2\2\u00c2\u00bb\3\2\2\2\u00c3 \3\2\2\2\u00c4\u00c5\7U\2\2\u00c5"+
		"\u00c6\7n\2\2\u00c6\u00c7\7c\2\2\u00c7\u00c8\7e\2\2\u00c8\u00cf\7m\2\2"+
		"\u00c9\u00ca\7u\2\2\u00ca\u00cb\7n\2\2\u00cb\u00cc\7c\2\2\u00cc\u00cd"+
		"\7e\2\2\u00cd\u00cf\7m\2\2\u00ce\u00c4\3\2\2\2\u00ce\u00c9\3\2\2\2\u00cf"+
		"\"\3\2\2\2\u00d0\u00d2\t\3\2\2\u00d1\u00d0\3\2\2\2\u00d2\u00d3\3\2\2\2"+
		"\u00d3\u00d1\3\2\2\2\u00d3\u00d4\3\2\2\2\u00d4$\3\2\2\2\u00d5\u00d7\t"+
		"\4\2\2\u00d6\u00d5\3\2\2\2\u00d7\u00d8\3\2\2\2\u00d8\u00d6\3\2\2\2\u00d8"+
		"\u00d9\3\2\2\2\u00d9&\3\2\2\2\u00da\u00dc\t\5\2\2\u00db\u00da\3\2\2\2"+
		"\u00dc\u00dd\3\2\2\2\u00dd\u00db\3\2\2\2\u00dd\u00de\3\2\2\2\u00de(\3"+
		"\2\2\2\u00df\u00e0\7\62\2\2\u00e0\u00e1\7z\2\2\u00e1\u00e5\3\2\2\2\u00e2"+
		"\u00e4\t\5\2\2\u00e3\u00e2\3\2\2\2\u00e4\u00e7\3\2\2\2\u00e5\u00e3\3\2"+
		"\2\2\u00e5\u00e6\3\2\2\2\u00e6*\3\2\2\2\u00e7\u00e5\3\2\2\2\u00e8\u00e9"+
		"\7j\2\2\u00e9\u00ea\7v\2\2\u00ea\u00eb\7v\2\2\u00eb\u00ec\7r\2\2\u00ec"+
		"\u00ed\7u\2\2\u00ed\u00ef\3\2\2\2\u00ee\u00f0\3\2\2\2\u00ef\u00ee\3\2"+
		"\2\2\u00f0\u00f1\3\2\2\2\u00f1\u00f2\3\2\2\2\u00f1\u00ef\3\2\2\2\u00f2"+
		",\3\2\2\2\u00f3\u00f5\t\6\2\2\u00f4\u00f3\3\2\2\2\u00f5\u00f6\3\2\2\2"+
		"\u00f6\u00f4\3\2\2\2\u00f6\u00f7\3\2\2\2\u00f7\u00f8\3\2\2\2\u00f8\u00f9"+
		"\b\27\2\2\u00f9.\3\2\2\2\20\2\61>v\u00a0\u00b2\u00c2\u00ce\u00d3\u00d8"+
		"\u00dd\u00e5\u00f1\u00f6\3\b\2\2";
	public static final ATN _ATN =
		new ATNDeserializer().deserialize(_serializedATN.toCharArray());
	static {
		_decisionToDFA = new DFA[_ATN.getNumberOfDecisions()];
		for (int i = 0; i < _ATN.getNumberOfDecisions(); i++) {
			_decisionToDFA[i] = new DFA(_ATN.getDecisionState(i), i);
		}
	}
}