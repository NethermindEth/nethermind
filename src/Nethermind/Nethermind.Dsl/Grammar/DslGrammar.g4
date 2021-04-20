grammar DslGrammar;

expression : OPERATOR ID ;

OPERATOR : SOURCE | WATCH | WHERE | PUBLISH ;

SOURCE : 'SOURCE' ;
WATCH : 'WATCH' ;
WHERE : 'WHERE' ;
PUBLISH : 'PUBLISH' ;

ID : [a-z]+ ;
DIGIT: [0-9]+;
WS : [ \t\r\n]+ -> skip ; // skip spaces, tabs, newlines
