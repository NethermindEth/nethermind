grammar DslGrammar;

init: (expression)* ;
expression : OPERATOR (OPERATOR_VALUE | assign) ;
assign : OPERATOR_VALUE '==' (DIGIT | OPERATOR_VALUE | ADDRESS) ; 

OPERATOR : SOURCE | WATCH | WHERE | PUBLISH ;

SOURCE : 'SOURCE' ;
WATCH : 'WATCH' ;
WHERE : 'WHERE' ;
PUBLISH : 'PUBLISH' ;

OPERATOR_VALUE : [a-zA-Z]+ ;
DIGIT: [0-9]+;
ADDRESS : '0x'[a-fA-F0-9]* ;
WS : [ \t\r\n]+ -> skip ; // skip spaces, tabs, newlines
