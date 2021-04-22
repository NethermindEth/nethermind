grammar DslGrammar;

init: (expression)* ;
expression : OPERATOR OPERATOR_VALUE | condition ;
condition : WHERE OPERATOR_VALUE ARITHMETIC_SYMBOL (DIGIT | OPERATOR_VALUE | ADDRESS) ; 

OPERATOR : SOURCE | WATCH | WHERE | PUBLISH ;
ARITHMETIC_SYMBOL : '==' | '!=' | '<' | '>' | '<=' | '>=' ;

SOURCE : 'SOURCE' ;
WATCH : 'WATCH' ;
WHERE : 'WHERE' ;
PUBLISH : 'PUBLISH' ;

OPERATOR_VALUE : [a-zA-Z]+ ;
DIGIT: [0-9]+;
ADDRESS : '0x'[a-fA-F0-9]* ;
WS : [ \t\r\n]+ -> skip ; // skip spaces, tabs, newlines
