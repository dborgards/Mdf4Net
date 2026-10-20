using System;

namespace Mdf4Net.Internal;

/// <summary>
/// The byte transposition of DZ zip type 1. The data is read as a matrix of
/// <c>rows = length / columns</c> rows with <c>columns</c> bytes each and written column by column;
/// the <c>length % columns</c> trailing bytes are copied unchanged.
/// </summary>
internal static class Transposition
{
    public static byte[] Transpose(byte[] data, int offset, int length, int columns)
    {
        var result = new byte[length];
        if (columns <= 1)
        {
            Buffer.BlockCopy(data, offset, result, 0, length);
            return result;
        }

        int rows = length / columns;
        for (int r = 0; r < rows; r++)
        {
            int source = offset + (r * columns);
            for (int c = 0; c < columns; c++)
            {
                result[(c * rows) + r] = data[source + c];
            }
        }

        int done = rows * columns;
        Buffer.BlockCopy(data, offset + done, result, done, length - done);
        return result;
    }

    public static byte[] Untranspose(byte[] data, int columns)
    {
        int length = data.Length;
        if (columns <= 1)
        {
            return data;
        }

        var result = new byte[length];
        int rows = length / columns;
        for (int c = 0; c < columns; c++)
        {
            int source = c * rows;
            for (int r = 0; r < rows; r++)
            {
                result[(r * columns) + c] = data[source + r];
            }
        }

        int done = rows * columns;
        Buffer.BlockCopy(data, done, result, done, length - done);
        return result;
    }
}
