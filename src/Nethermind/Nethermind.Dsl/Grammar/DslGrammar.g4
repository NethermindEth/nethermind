grammar DslGrammar;

tree: (expression)* ;
expression : watchExpression | whereExpression | publishExpression | andCondition | orCondition ;
watchExpression : WATCH WORD ;
whereExpression : WHERE condition ;
publishExpression : PUBLISH PUBLISH_VALUE ( URL | WORD | DIGIT );
condition : WORD BOOLEAN_OPERATOR ( WORD | DIGIT | ADDRESS | BYTECODE ) ;
andCondition : AND condition ;
orCondition : OR condition ;

BOOLEAN_OPERATOR : ARITHMETIC_SYMBOL | CONTAINS ;
ARITHMETIC_SYMBOL : '==' | '!=' | '<' | '>' | '<=' | '>=' | IS | NOT ;

WATCH : 'WATCH' ;
WHERE : 'WHERE' ;
PUBLISH : 'PUBLISH' ;
AND : 'AND' ;
OR : 'OR' ;
CONTAINS : 'CONTAINS' ;
IS: 'IS' ;
NOT: 'NOT' ;

PUBLISH_VALUE : WEBSOCKETS | TELEGRAM | DISCORD | SLACK;
WEBSOCKETS : 'WebSockets' | 'webSockets' | 'websockets' | 'Websockets' ;
TELEGRAM : 'Telegram' | 'telegram' ;
DISCORD : 'Discord' | 'discord' ;
SLACK : 'Slack' | 'slack' ;


WORD : [a-zA-Z]+ ;
DIGIT : [0-9]+;
BYTECODE : [a-fA-F0-9]+ ;
ADDRESS : '0x'[a-fA-F0-9]* ;
URL: 'https'()+? ;
WS : [ \t\r\n]+ -> skip ; // skip spaces, tabs, newlines
