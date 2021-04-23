grammar DslGrammar;

init: (expression | condition)* ;
expression : OPERATOR WORD ;
condition : WHERE WORD ARITHMETIC_SYMBOL (DIGIT | WORD | ADDRESS); 

OPERATOR : SOURCE | WATCH | WHERE | PUBLISH ;
ARITHMETIC_SYMBOL : '==' | '!=' | '<' | '>' | '<=' | '>=' ;

SOURCE : 'SOURCE' ;
WATCH : 'WATCH' ;
WHERE : 'WHERE' ;
PUBLISH : 'PUBLISH' ;

WORD : [a-zA-Z]+ ;
DIGIT : [0-9]+;
ADDRESS : '0x'[a-fA-F0-9]* ;
WS : [ \t\r\n]+ -> skip ; // skip spaces, tabs, newlines
