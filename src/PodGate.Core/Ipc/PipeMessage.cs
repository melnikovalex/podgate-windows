using System.Text;

namespace PodGate.Core.Ipc;

/// <summary>
/// One message = one UTF-8 line, written and read as bytes.
///
/// This is deliberately hand-rolled. A duplex byte pipe has no end-of-message marker, so
/// JsonSerializer.DeserializeAsync blocks until the stream closes, and layering StreamWriter over the
/// pipe left the newline sitting in a buffer until the client gave up 30 s later. Bytes in, bytes out,
/// with an explicit flush, behaves the same every time.
/// </summary>
public static class PipeMessage
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private const byte Newline = (byte)'\n';
    private const int MaxMessageBytes = 64 * 1024;

    public static async Task WriteAsync(Stream stream, string message, CancellationToken cancellationToken)
    {
        byte[] payload = Utf8.GetBytes(message + "\n");
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>Returns null when the other end closed before sending a complete line.</summary>
    public static async Task<string?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        using var message = new MemoryStream();

        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) return null;

            for (int i = 0; i < read; i++)
            {
                if (buffer[i] == Newline)
                {
                    message.Write(buffer, 0, i);
                    return Utf8.GetString(message.ToArray()).TrimEnd('\r');
                }
            }

            message.Write(buffer, 0, read);
            if (message.Length > MaxMessageBytes) throw new InvalidDataException("message too long");
        }
    }
}
