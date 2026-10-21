using System;
using System.IO;

namespace Mdf4Net.Tests.Infrastructure;

/// <summary>A memory stream whose n-th write from now on fails once, like a full disk would.</summary>
internal sealed class FailingStream : MemoryStream
{
    private int _writesUntilFailure = -1;

    public int Writes { get; private set; }

    public void FailOnWrite(int writeNumber) => _writesUntilFailure = writeNumber;

    public override void Write(byte[] buffer, int offset, int count)
    {
        Writes++;
        if (_writesUntilFailure > 0 && --_writesUntilFailure == 0)
        {
            throw new IOException("Simulated write failure.");
        }

        base.Write(buffer, offset, count);
    }

    public override void WriteByte(byte value) => Write(new[] { value }, 0, 1);
}
