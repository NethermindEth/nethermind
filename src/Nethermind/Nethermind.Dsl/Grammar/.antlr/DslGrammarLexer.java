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
		IS=7, NOT=8, WORD=9, DIGIT=10, ADDRESS=11, WS=12;
	public static String[] channelNames = {
		"DEFAULT_TOKEN_CHANNEL", "HIDDEN"
	};

	public static String[] modeNames = {
		"DEFAULT_MODE"
	};

	private static String[] makeRuleNames() {
		return new String[] {
			"OPERATOR", "ARITHMETIC_SYMBOL", "SOURCE", "WATCH", "WHERE", "PUBLISH", 
			"IS", "NOT", "WORD", "DIGIT", "ADDRESS", "WS"
		};
	}
	public static final String[] ruleNames = makeRuleNames();

	private static String[] makeLiteralNames() {
		return new String[] {
			null, null, null, "'SOURCE'", "'WATCH'", "'WHERE'", "'PUBLISH'", "'IS'", 
			"'NOT'"
		};
	}
	private static final String[] _LITERAL_NAMES = makeLiteralNames();
	private static String[] makeSymbolicNames() {
		return new String[] {
			null, "OPERATOR", "ARITHMETIC_SYMBOL", "SOURCE", "WATCH", "WHERE", "PUBLISH", 
			"IS", "NOT", "WORD", "DIGIT", "ADDRESS", "WS"
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
		"\3\u608b\ua72a\u8133\ub9ed\u417c\u3be7\u7786\u5964\2\16f\b\1\4\2\t\2\4"+
		"\3\t\3\4\4\t\4\4\5\t\5\4\6\t\6\4\7\t\7\4\b\t\b\4\t\t\t\4\n\t\n\4\13\t"+
		"\13\4\f\t\f\4\r\t\r\3\2\3\2\3\2\3\2\5\2 \n\2\3\3\3\3\3\3\3\3\3\3\3\3\3"+
		"\3\5\3)\n\3\3\4\3\4\3\4\3\4\3\4\3\4\3\4\3\5\3\5\3\5\3\5\3\5\3\5\3\6\3"+
		"\6\3\6\3\6\3\6\3\6\3\7\3\7\3\7\3\7\3\7\3\7\3\7\3\7\3\b\3\b\3\b\3\t\3\t"+
		"\3\t\3\t\3\n\6\nN\n\n\r\n\16\nO\3\13\6\13S\n\13\r\13\16\13T\3\f\3\f\3"+
		"\f\3\f\7\f[\n\f\f\f\16\f^\13\f\3\r\6\ra\n\r\r\r\16\rb\3\r\3\r\2\2\16\3"+
		"\3\5\4\7\5\t\6\13\7\r\b\17\t\21\n\23\13\25\f\27\r\31\16\3\2\7\4\2>>@@"+
		"\4\2C\\c|\3\2\62;\5\2\62;CHch\5\2\13\f\17\17\"\"\2p\2\3\3\2\2\2\2\5\3"+
		"\2\2\2\2\7\3\2\2\2\2\t\3\2\2\2\2\13\3\2\2\2\2\r\3\2\2\2\2\17\3\2\2\2\2"+
		"\21\3\2\2\2\2\23\3\2\2\2\2\25\3\2\2\2\2\27\3\2\2\2\2\31\3\2\2\2\3\37\3"+
		"\2\2\2\5(\3\2\2\2\7*\3\2\2\2\t\61\3\2\2\2\13\67\3\2\2\2\r=\3\2\2\2\17"+
		"E\3\2\2\2\21H\3\2\2\2\23M\3\2\2\2\25R\3\2\2\2\27V\3\2\2\2\31`\3\2\2\2"+
		"\33 \5\7\4\2\34 \5\t\5\2\35 \5\13\6\2\36 \5\r\7\2\37\33\3\2\2\2\37\34"+
		"\3\2\2\2\37\35\3\2\2\2\37\36\3\2\2\2 \4\3\2\2\2!)\t\2\2\2\"#\7>\2\2#)"+
		"\7?\2\2$%\7@\2\2%)\7?\2\2&)\5\17\b\2\')\5\21\t\2(!\3\2\2\2(\"\3\2\2\2"+
		"($\3\2\2\2(&\3\2\2\2(\'\3\2\2\2)\6\3\2\2\2*+\7U\2\2+,\7Q\2\2,-\7W\2\2"+
		"-.\7T\2\2./\7E\2\2/\60\7G\2\2\60\b\3\2\2\2\61\62\7Y\2\2\62\63\7C\2\2\63"+
		"\64\7V\2\2\64\65\7E\2\2\65\66\7J\2\2\66\n\3\2\2\2\678\7Y\2\289\7J\2\2"+
		"9:\7G\2\2:;\7T\2\2;<\7G\2\2<\f\3\2\2\2=>\7R\2\2>?\7W\2\2?@\7D\2\2@A\7"+
		"N\2\2AB\7K\2\2BC\7U\2\2CD\7J\2\2D\16\3\2\2\2EF\7K\2\2FG\7U\2\2G\20\3\2"+
		"\2\2HI\7P\2\2IJ\7Q\2\2JK\7V\2\2K\22\3\2\2\2LN\t\3\2\2ML\3\2\2\2NO\3\2"+
		"\2\2OM\3\2\2\2OP\3\2\2\2P\24\3\2\2\2QS\t\4\2\2RQ\3\2\2\2ST\3\2\2\2TR\3"+
		"\2\2\2TU\3\2\2\2U\26\3\2\2\2VW\7\62\2\2WX\7z\2\2X\\\3\2\2\2Y[\t\5\2\2"+
		"ZY\3\2\2\2[^\3\2\2\2\\Z\3\2\2\2\\]\3\2\2\2]\30\3\2\2\2^\\\3\2\2\2_a\t"+
		"\6\2\2`_\3\2\2\2ab\3\2\2\2b`\3\2\2\2bc\3\2\2\2cd\3\2\2\2de\b\r\2\2e\32"+
		"\3\2\2\2\t\2\37(OT\\b\3\b\2\2";
	public static final ATN _ATN =
		new ATNDeserializer().deserialize(_serializedATN.toCharArray());
	static {
		_decisionToDFA = new DFA[_ATN.getNumberOfDecisions()];
		for (int i = 0; i < _ATN.getNumberOfDecisions(); i++) {
			_decisionToDFA[i] = new DFA(_ATN.getDecisionState(i), i);
		}
	}
}