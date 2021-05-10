grammar DslGrammar;

init: (expression)* ;
expression : sourceExpression | watchExpression | whereExpression | publishExpression | andCondition | orCondition ;
sourceExpression : SOURCE WORD ;
watchExpression : WATCH WORD ;
whereExpression : WHERE condition ;
publishExpression : PUBLISH (WEBSOCKETS | LOG_PUBLISHER) WORD ;
andCondition : AND condition ;
orCondition : OR condition ;

condition : WORD ( ARITHMETIC_SYMBOL | CONTAINS ) (DIGIT | WORD | ADDRESS) ; 

ARITHMETIC_SYMBOL : '==' | '!=' | '<' | '>' | '<=' | '>=' ;

SOURCE : 'SOURCE' ;
WATCH : 'WATCH' ;
WHERE : 'WHERE' ;
PUBLISH : 'PUBLISH' ;
AND : 'AND' ;
OR : 'OR' ;
CONTAINS : 'CONTAINS' ;

WEBSOCKETS : 'WebSockets' | 'webSockets' | 'websockets' ;
LOG_PUBLISHER : 'LogPublisher' | 'logPublisher' | 'logpublisher' ;

WORD : [a-zA-Z]+ ;
DIGIT : [0-9]+;
ADDRESS : '0x'[a-fA-F0-9]* ;
WS : [ \t\r\n]+ -> skip ; // skip spaces, tabs, newlines
