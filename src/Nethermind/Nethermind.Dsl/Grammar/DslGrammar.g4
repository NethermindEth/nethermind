grammar DslGrammar;

init: (expression | condition)* ;
expression : OPERATOR WORD ;
source_expression : SOURCE WORD ;
watch_expression : WATCH WORD ;
where_expression : WHERE condition ;
publish_expression : PUBLISH WEBSOCKETS | LOG_PUBLISHER WORD ;

condition : WORD ( ARITHMETIC_SYMBOL | CONTAINS ) (DIGIT | WORD | ADDRESS) 
	   | (BOOLEAN_OPERATOR) condition ; 

OPERATOR : SOURCE | WATCH | WHERE | PUBLISH ;
BOOLEAN_OPERATOR : AND | OR ;
ARITHMETIC_SYMBOL : '==' | '!=' | '<' | '>' | '<=' | '>=' ;

SOURCE : 'SOURCE' ;
WATCH : 'WATCH' ;
WHERE : 'WHERE' ;
PUBLISH : 'PUBLISH' ;
AND : 'AND' ;
OR : 'OR' ;
CONTAINS : 'CONTAINS' ;

WORD : [a-zA-Z]+ ;
DIGIT : [0-9]+;
ADDRESS : '0x'[a-fA-F0-9]* ;
WEBSOCKETS : (?i)WebSockets ;
LOG_PUBLISHER : (?i)LogPublisher ;
WS : [ \t\r\n]+ -> skip ; // skip spaces, tabs, newlines
