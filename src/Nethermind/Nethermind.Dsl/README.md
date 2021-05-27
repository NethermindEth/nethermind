DSL plugin based on ANTLR4. 
In order to generate ANTLR files while changing grammar file you will have to run: 
	`export CLASSPATH=".:/usr/local/lib/antlr-4.9.2-complete.jar:$CLASSPATH"`
	`alias antlr4='java -jar /usr/local/lib/antlr-4.9.2-complete.jar'`
	For testing: `alias grun='java org.antlr.v4.gui.TestRig'`
	For ANTLR based c# files: `antlr4 -Dlanguage=CSharp -o ../ANTLR DslGrammar.g4` in Grammar directory (this will generate all files you need for working with ANTLR tree listener).

ANTLR4 documentation can be find here: https://www.antlr.org/
