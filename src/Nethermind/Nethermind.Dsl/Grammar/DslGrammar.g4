grammar DslGrammar;

tree: (expression)* ;
expression : sourceExpression | watchExpression | whereExpression | publishExpression | andCondition | orCondition ;
sourceExpression : SOURCE WORD ;
watchExpression : WATCH WORD ;
whereExpression : WHERE condition ;
publishExpression : PUBLISH PUBLISH_VALUE WORD ;
condition : WORD BOOLEAN_OPERATOR ( WORD | DIGIT | ADDRESS ) ; 
andCondition : AND condition ;
orCondition : OR condition ;

SOURCE : 'SOURCE' ;
WATCH : 'WATCH' ;
WHERE : 'WHERE' ;
PUBLISH : 'PUBLISH' ;
AND : 'AND' ;
OR : 'OR' ;
CONTAINS : 'CONTAINS' ;

BOOLEAN_OPERATOR : ARITHMETIC_SYMBOL | CONTAINS ;
ARITHMETIC_SYMBOL : '==' | '!=' | '<' | '>' | '<=' | '>=' ;

PUBLISH_VALUE : WEBSOCKETS | LOG_PUBLISHER ;
WEBSOCKETS : 'WebSockets' | 'webSockets' | 'websockets' ;
LOG_PUBLISHER : 'LogPublisher' | 'logPublisher' | 'logpublisher' ;

WORD : [a-zA-Z]+ ;
DIGIT : [0-9]+;
ADDRESS : '0x'[a-fA-F0-9]* ;
WS : [ \t\r\n]+ -> skip ; // skip spaces, tabs, newlines
