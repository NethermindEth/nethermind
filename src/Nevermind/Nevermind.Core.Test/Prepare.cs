namespace Nevermind.Core.Test
{
    public class Prepare
    {
        public static Prepare A { get; } = new Prepare();

        public static Prepare An => A;
    }
}