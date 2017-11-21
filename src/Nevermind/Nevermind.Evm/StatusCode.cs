namespace Nevermind.Evm
{
    public class StatusCode
    {
        public const byte Failure = 0;
        public static byte[] FailureBytes = new byte[] { 0 };
        public const byte Success = 1;
        public static byte[] SuccessBytes = new byte[] { 1 };
    }
}