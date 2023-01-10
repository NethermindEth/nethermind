namespace Nethermind.Merge.AuRa
{
    public class EmptyBlockProductionException : System.Exception
    {
        public EmptyBlockProductionException(string message)
            : base($"Couldn't produce empty block: {message}") { }
    }
}
