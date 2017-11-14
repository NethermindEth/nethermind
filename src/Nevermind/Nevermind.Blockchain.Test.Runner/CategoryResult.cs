namespace Nevermind.Blockchain.Test.Runner
{
    public class CategoryResult
    {
        public CategoryResult(long totalMs, string[] failingTests)
        {
            TotalMs = totalMs;
            FailingTests = failingTests;
        }

        public long TotalMs { get; set; }
        public string[] FailingTests { get; set; }
    }
}