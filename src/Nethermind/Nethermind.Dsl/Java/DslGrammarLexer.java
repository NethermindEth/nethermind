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
		OPERATOR=1, ARITHMETIC_SYMBOL=2, SOURCE=3, WATCH=4, WHERE=5, PUBLISH=6, 
		WORD=7, DIGIT=8, ADDRESS=9, WS=10;
	public static String[] channelNames = {
		"DEFAULT_TOKEN_CHANNEL", "HIDDEN"
	};

	public static String[] modeNames = {
		"DEFAULT_MODE"
	};

	private static String[] makeRuleNames() {
		return new String[] {
			"OPERATOR", "ARITHMETIC_SYMBOL", "SOURCE", "WATCH", "WHERE", "PUBLISH", 
			"WORD", "DIGIT", "ADDRESS", "WS"
		};
	}
	public static final String[] ruleNames = makeRuleNames();

	private static String[] makeLiteralNames() {
		return new String[] {
			null, null, null, "'SOURCE'", "'WATCH'", "'WHERE'", "'PUBLISH'"
		};
	}
	private static final String[] _LITERAL_NAMES = makeLiteralNames();
	private static String[] makeSymbolicNames() {
		return new String[] {
			null, "OPERATOR", "ARITHMETIC_SYMBOL", "SOURCE", "WATCH", "WHERE", "PUBLISH", 
			"WORD", "DIGIT", "ADDRESS", "WS"
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
		"\3\u608b\ua72a\u8133\ub9ed\u417c\u3be7\u7786\u5964\2\f\\\b\1\4\2\t\2\4"+
		"\3\t\3\4\4\t\4\4\5\t\5\4\6\t\6\4\7\t\7\4\b\t\b\4\t\t\t\4\n\t\n\4\13\t"+
		"\13\3\2\3\2\3\2\5\2\33\n\2\3\3\3\3\3\3\3\3\3\3\3\3\3\3\3\3\3\3\5\3&\n"+
		"\3\3\4\3\4\3\4\3\4\3\4\3\4\3\4\3\5\3\5\3\5\3\5\3\5\3\5\3\6\3\6\3\6\3\6"+
		"\3\6\3\6\3\7\3\7\3\7\3\7\3\7\3\7\3\7\3\7\3\b\6\bD\n\b\r\b\16\bE\3\t\6"+
		"\tI\n\t\r\t\16\tJ\3\n\3\n\3\n\3\n\7\nQ\n\n\f\n\16\nT\13\n\3\13\6\13W\n"+
		"\13\r\13\16\13X\3\13\3\13\2\2\f\3\3\5\4\7\5\t\6\13\7\r\b\17\t\21\n\23"+
		"\13\25\f\3\2\7\4\2>>@@\4\2C\\c|\3\2\62;\5\2\62;CHch\5\2\13\f\17\17\"\""+
		"\2e\2\3\3\2\2\2\2\5\3\2\2\2\2\7\3\2\2\2\2\t\3\2\2\2\2\13\3\2\2\2\2\r\3"+
		"\2\2\2\2\17\3\2\2\2\2\21\3\2\2\2\2\23\3\2\2\2\2\25\3\2\2\2\3\32\3\2\2"+
		"\2\5%\3\2\2\2\7\'\3\2\2\2\t.\3\2\2\2\13\64\3\2\2\2\r:\3\2\2\2\17C\3\2"+
		"\2\2\21H\3\2\2\2\23L\3\2\2\2\25V\3\2\2\2\27\33\5\7\4\2\30\33\5\t\5\2\31"+
		"\33\5\r\7\2\32\27\3\2\2\2\32\30\3\2\2\2\32\31\3\2\2\2\33\4\3\2\2\2\34"+
		"\35\7?\2\2\35&\7?\2\2\36\37\7#\2\2\37&\7?\2\2 &\t\2\2\2!\"\7>\2\2\"&\7"+
		"?\2\2#$\7@\2\2$&\7?\2\2%\34\3\2\2\2%\36\3\2\2\2% \3\2\2\2%!\3\2\2\2%#"+
		"\3\2\2\2&\6\3\2\2\2\'(\7U\2\2()\7Q\2\2)*\7W\2\2*+\7T\2\2+,\7E\2\2,-\7"+
		"G\2\2-\b\3\2\2\2./\7Y\2\2/\60\7C\2\2\60\61\7V\2\2\61\62\7E\2\2\62\63\7"+
		"J\2\2\63\n\3\2\2\2\64\65\7Y\2\2\65\66\7J\2\2\66\67\7G\2\2\678\7T\2\28"+
		"9\7G\2\29\f\3\2\2\2:;\7R\2\2;<\7W\2\2<=\7D\2\2=>\7N\2\2>?\7K\2\2?@\7U"+
		"\2\2@A\7J\2\2A\16\3\2\2\2BD\t\3\2\2CB\3\2\2\2DE\3\2\2\2EC\3\2\2\2EF\3"+
		"\2\2\2F\20\3\2\2\2GI\t\4\2\2HG\3\2\2\2IJ\3\2\2\2JH\3\2\2\2JK\3\2\2\2K"+
		"\22\3\2\2\2LM\7\62\2\2MN\7z\2\2NR\3\2\2\2OQ\t\5\2\2PO\3\2\2\2QT\3\2\2"+
		"\2RP\3\2\2\2RS\3\2\2\2S\24\3\2\2\2TR\3\2\2\2UW\t\6\2\2VU\3\2\2\2WX\3\2"+
		"\2\2XV\3\2\2\2XY\3\2\2\2YZ\3\2\2\2Z[\b\13\2\2[\26\3\2\2\2\t\2\32%EJRX"+
		"\3\b\2\2";
	public static final ATN _ATN =
		new ATNDeserializer().deserialize(_serializedATN.toCharArray());
	static {
		_decisionToDFA = new DFA[_ATN.getNumberOfDecisions()];
		for (int i = 0; i < _ATN.getNumberOfDecisions(); i++) {
			_decisionToDFA[i] = new DFA(_ATN.getDecisionState(i), i);
		}
	}
}