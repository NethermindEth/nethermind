namespace Nevermind.Blockchain.Test.Runner
{
    public interface ITestInRunner
    {
        CategoryResult RunTests(string subset, string testWildcard, int iterations = 1);
    }
}