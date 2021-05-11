grammar DslGrammar;

tree: (expression)* ;
expression : sourceExpression | watchExpression | whereExpression | publishExpression | andCondition | orCondition ;
sourceExpression : SOURCE WORD ;
watchExpression : WATCH WORD ;
whereExpression : WHERE condition ;
publishExpression : PUBLISH PUBLISH_VALUE WORD ;
condition : WORD BOOLEAN_OPERATOR  ; 
andCondition : AND condition ;
orCondition : OR condition ;

WORD : [a-zA-Z]+ ;
DIGIT : [0-9]+;
ADDRESS : '0x'[a-fA-F0-9]* ;
WS : [ \t\r\n]+ -> skip ; // skip spaces, tabs, newlines

BOOLEAN_OPERATOR : ARITHMETIC_SYMBOL | CONTAINS ;
ARITHMETIC_SYMBOL : '==' | '!=' | '<' | '>' | '<=' | '>=' ;
CONDITION_VALUE : WORD | DIGIT | ADDRESS ;

SOURCE : 'SOURCE' ;
WATCH : 'WATCH' ;
WHERE : 'WHERE' ;
PUBLISH : 'PUBLISH' ;
AND : 'AND' ;
OR : 'OR' ;
CONTAINS : 'CONTAINS' ;

PUBLISH_VALUE : WEBSOCKETS | LOG_PUBLISHER ;
WEBSOCKETS : 'WebSockets' | 'webSockets' | 'websockets' ;
LOG_PUBLISHER : 'LogPublisher' | 'logPublisher' | 'logpublisher' ;