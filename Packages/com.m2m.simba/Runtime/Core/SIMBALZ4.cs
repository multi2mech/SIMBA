using System;
using System.IO;

namespace M2M.SIMBA
{
    internal static class SIMBALZ4
    {
        // Decoder for a raw LZ4 block (the format produced by lz4.block.compress
        // with store_size=False). It intentionally has no external dependency.
        public static byte[] Decode(byte[] source, int decodedLength)
        {
            if (decodedLength < 0)
                throw new InvalidDataException("Invalid LZ4 decoded length.");

            byte[] destination = new byte[decodedLength];
            int sourceIndex = 0;
            int destinationIndex = 0;

            while (sourceIndex < source.Length)
            {
                byte token = source[sourceIndex++];

                int literalLength = token >> 4;
                if (literalLength == 15)
                {
                    byte extension;
                    do
                    {
                        if (sourceIndex >= source.Length)
                            throw new InvalidDataException("Truncated LZ4 literal length.");
                        extension = source[sourceIndex++];
                        literalLength += extension;
                    }
                    while (extension == 255);
                }

                if (sourceIndex + literalLength > source.Length ||
                    destinationIndex + literalLength > destination.Length)
                {
                    throw new InvalidDataException("Invalid LZ4 literal run.");
                }

                Buffer.BlockCopy(
                    source,
                    sourceIndex,
                    destination,
                    destinationIndex,
                    literalLength);

                sourceIndex += literalLength;
                destinationIndex += literalLength;

                if (sourceIndex >= source.Length)
                    break;

                if (sourceIndex + 2 > source.Length)
                    throw new InvalidDataException("Truncated LZ4 match offset.");

                int offset = source[sourceIndex] | (source[sourceIndex + 1] << 8);
                sourceIndex += 2;

                if (offset <= 0 || offset > destinationIndex)
                    throw new InvalidDataException("Invalid LZ4 match offset.");

                int matchLength = token & 0x0F;
                if (matchLength == 15)
                {
                    byte extension;
                    do
                    {
                        if (sourceIndex >= source.Length)
                            throw new InvalidDataException("Truncated LZ4 match length.");
                        extension = source[sourceIndex++];
                        matchLength += extension;
                    }
                    while (extension == 255);
                }
                matchLength += 4;

                if (destinationIndex + matchLength > destination.Length)
                    throw new InvalidDataException("Invalid LZ4 match run.");

                int matchIndex = destinationIndex - offset;
                for (int i = 0; i < matchLength; i++)
                    destination[destinationIndex++] = destination[matchIndex + i];
            }

            if (destinationIndex != decodedLength)
                throw new InvalidDataException(
                    $"LZ4 decoded {destinationIndex} bytes, expected {decodedLength}.");

            return destination;
        }
    }
}
