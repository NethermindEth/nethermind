namespace Nevermind.Blockchain.Test.Runner
{
    public interface ITestInRunner
    {
        CategoryResult RunTests(string subset, int iterations = 1);
    }
}