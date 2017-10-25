using System;

namespace Nevermind.Core.Sugar
{
    public static class Pow2
    {
        public static int To(int pow)
        {
            switch (pow)
            {
                case 0:
                    return 1;
                case 1:
                    return 2;
                case 2:
                    return 4;
                case 3:
                    return 8;
                case 4:
                    return 16;
                case 5:
                    return 32;
                case 6:
                    return 64;
                case 7:
                    return 128;
                default:
                    throw new NotImplementedException();
            }
        }
    }
}