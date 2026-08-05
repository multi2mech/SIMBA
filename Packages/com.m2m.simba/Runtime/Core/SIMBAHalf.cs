using System;

namespace M2M.SIMBA
{
    internal static class SIMBAHalf
    {
        public static float ToSingle(ushort value)
        {
            uint sign = (uint)(value & 0x8000) << 16;
            int exponent = (value >> 10) & 0x1F;
            uint mantissa = (uint)value & 0x03FFu;
            uint bits;

            if (exponent == 0)
            {
                if (mantissa == 0)
                {
                    bits = sign;
                }
                else
                {
                    exponent = -14;
                    while ((mantissa & 0x0400u) == 0)
                    {
                        mantissa <<= 1;
                        exponent--;
                    }

                    mantissa &= 0x03FFu;
                    uint singleExponent = (uint)(exponent + 127);
                    bits = sign |
                           (singleExponent << 23) |
                           (mantissa << 13);
                }
            }
            else if (exponent == 31)
            {
                bits = sign |
                       0x7F800000u |
                       (mantissa << 13);
            }
            else
            {
                uint singleExponent =
                    (uint)(exponent - 15 + 127);

                bits = sign |
                       (singleExponent << 23) |
                       (mantissa << 13);
            }

            return BitConverter.Int32BitsToSingle(
                unchecked((int)bits));
        }
    }
}
