grammar DslGrammar;

init: (expression)* ;
expression : OPERATOR WORD | condition ;
condition : WHERE WORD ARITHMETIC_SYMBOL CONDITION_MATCHER; 

OPERATOR : SOURCE | WATCH | WHERE | PUBLISH ;
ARITHMETIC_SYMBOL : '==' | '!=' | '<' | '>' | '<=' | '>=' ;
CONDITION_MATCHER : DIGIT | WORD | ADDRESS ;

SOURCE : 'SOURCE' ;
WATCH : 'WATCH' ;
WHERE : 'WHERE' ;
PUBLISH : 'PUBLISH' ;

WORD : [a-zA-Z]+ ;
DIGIT : [0-9]+;
ADDRESS : '0x'[a-fA-F0-9]* ;
WS : [ \t\r\n]+ -> skip ; // skip spaces, tabs, newlines
