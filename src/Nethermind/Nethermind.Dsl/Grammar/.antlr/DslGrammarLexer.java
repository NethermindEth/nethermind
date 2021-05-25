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
		OPERATOR=1, ARITHMETIC_SYMBOL=2, SOURCE=3, WATCH=4, WHERE=5, PUBLISH=6, 
		IS=7, WORD=8, DIGIT=9, ADDRESS=10, WS=11;
	public static String[] channelNames = {
		"DEFAULT_TOKEN_CHANNEL", "HIDDEN"
	};

	public static String[] modeNames = {
		"DEFAULT_MODE"
	};

	private static String[] makeRuleNames() {
		return new String[] {
			"OPERATOR", "ARITHMETIC_SYMBOL", "SOURCE", "WATCH", "WHERE", "PUBLISH", 
			"IS", "WORD", "DIGIT", "ADDRESS", "WS"
		};
	}
	public static final String[] ruleNames = makeRuleNames();

	private static String[] makeLiteralNames() {
		return new String[] {
			null, null, null, "'SOURCE'", "'WATCH'", "'WHERE'", "'PUBLISH'", "'IS'"
		};
	}
	private static final String[] _LITERAL_NAMES = makeLiteralNames();
	private static String[] makeSymbolicNames() {
		return new String[] {
			null, "OPERATOR", "ARITHMETIC_SYMBOL", "SOURCE", "WATCH", "WHERE", "PUBLISH", 
			"IS", "WORD", "DIGIT", "ADDRESS", "WS"
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
		"\3\u608b\ua72a\u8133\ub9ed\u417c\u3be7\u7786\u5964\2\rc\b\1\4\2\t\2\4"+
		"\3\t\3\4\4\t\4\4\5\t\5\4\6\t\6\4\7\t\7\4\b\t\b\4\t\t\t\4\n\t\n\4\13\t"+
		"\13\4\f\t\f\3\2\3\2\3\2\3\2\3\2\5\2\37\n\2\3\3\3\3\3\3\3\3\3\3\3\3\3\3"+
		"\3\3\3\3\5\3*\n\3\3\4\3\4\3\4\3\4\3\4\3\4\3\4\3\5\3\5\3\5\3\5\3\5\3\5"+
		"\3\6\3\6\3\6\3\6\3\6\3\6\3\7\3\7\3\7\3\7\3\7\3\7\3\7\3\7\3\b\3\b\3\b\3"+
		"\t\6\tK\n\t\r\t\16\tL\3\n\6\nP\n\n\r\n\16\nQ\3\13\3\13\3\13\3\13\7\13"+
		"X\n\13\f\13\16\13[\13\13\3\f\6\f^\n\f\r\f\16\f_\3\f\3\f\2\2\r\3\3\5\4"+
		"\7\5\t\6\13\7\r\b\17\t\21\n\23\13\25\f\27\r\3\2\7\4\2>>@@\4\2C\\c|\3\2"+
		"\62;\5\2\62;CHch\5\2\13\f\17\17\"\"\2n\2\3\3\2\2\2\2\5\3\2\2\2\2\7\3\2"+
		"\2\2\2\t\3\2\2\2\2\13\3\2\2\2\2\r\3\2\2\2\2\17\3\2\2\2\2\21\3\2\2\2\2"+
		"\23\3\2\2\2\2\25\3\2\2\2\2\27\3\2\2\2\3\36\3\2\2\2\5)\3\2\2\2\7+\3\2\2"+
		"\2\t\62\3\2\2\2\138\3\2\2\2\r>\3\2\2\2\17F\3\2\2\2\21J\3\2\2\2\23O\3\2"+
		"\2\2\25S\3\2\2\2\27]\3\2\2\2\31\37\5\7\4\2\32\37\5\t\5\2\33\37\5\13\6"+
		"\2\34\37\5\r\7\2\35\37\5\17\b\2\36\31\3\2\2\2\36\32\3\2\2\2\36\33\3\2"+
		"\2\2\36\34\3\2\2\2\36\35\3\2\2\2\37\4\3\2\2\2 !\7?\2\2!*\7?\2\2\"#\7#"+
		"\2\2#*\7?\2\2$*\t\2\2\2%&\7>\2\2&*\7?\2\2\'(\7@\2\2(*\7?\2\2) \3\2\2\2"+
		")\"\3\2\2\2)$\3\2\2\2)%\3\2\2\2)\'\3\2\2\2*\6\3\2\2\2+,\7U\2\2,-\7Q\2"+
		"\2-.\7W\2\2./\7T\2\2/\60\7E\2\2\60\61\7G\2\2\61\b\3\2\2\2\62\63\7Y\2\2"+
		"\63\64\7C\2\2\64\65\7V\2\2\65\66\7E\2\2\66\67\7J\2\2\67\n\3\2\2\289\7"+
		"Y\2\29:\7J\2\2:;\7G\2\2;<\7T\2\2<=\7G\2\2=\f\3\2\2\2>?\7R\2\2?@\7W\2\2"+
		"@A\7D\2\2AB\7N\2\2BC\7K\2\2CD\7U\2\2DE\7J\2\2E\16\3\2\2\2FG\7K\2\2GH\7"+
		"U\2\2H\20\3\2\2\2IK\t\3\2\2JI\3\2\2\2KL\3\2\2\2LJ\3\2\2\2LM\3\2\2\2M\22"+
		"\3\2\2\2NP\t\4\2\2ON\3\2\2\2PQ\3\2\2\2QO\3\2\2\2QR\3\2\2\2R\24\3\2\2\2"+
		"ST\7\62\2\2TU\7z\2\2UY\3\2\2\2VX\t\5\2\2WV\3\2\2\2X[\3\2\2\2YW\3\2\2\2"+
		"YZ\3\2\2\2Z\26\3\2\2\2[Y\3\2\2\2\\^\t\6\2\2]\\\3\2\2\2^_\3\2\2\2_]\3\2"+
		"\2\2_`\3\2\2\2`a\3\2\2\2ab\b\f\2\2b\30\3\2\2\2\t\2\36)LQY_\3\b\2\2";
	public static final ATN _ATN =
		new ATNDeserializer().deserialize(_serializedATN.toCharArray());
	static {
		_decisionToDFA = new DFA[_ATN.getNumberOfDecisions()];
		for (int i = 0; i < _ATN.getNumberOfDecisions(); i++) {
			_decisionToDFA[i] = new DFA(_ATN.getDecisionState(i), i);
		}
	}
}