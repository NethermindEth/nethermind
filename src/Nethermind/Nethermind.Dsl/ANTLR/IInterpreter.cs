namespace Nethermind.Dsl.ANTLR
{
    public interface IInterpreter
    {
        void AddSource(string value);
        void AddWatch(string value);
        void AddExpression(AntlrTokenType tokenType, string value);
        void AddCondition(string key, string symbol, string value);
        void AddPublisher(string value);
    }
}