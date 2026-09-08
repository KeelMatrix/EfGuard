using System.Text.Json;

namespace KeelMatrix.EfGuard;

internal static class ExtractionLimits
{
    internal const int MaxResponseBytes = 4 * 1024 * 1024;
}

internal sealed class ResponseSizeLimitExceededException() : IOException("The EF extraction worker response exceeds the 4 MiB limit.");

internal static class ExtractionResponse
{
    internal static async Task WriteAsync(string path, ExtractionResult result, JsonSerializerOptions options)
    {
        string temporaryPath = path + ".tmp";
        try
        {
            await using (FileStream file = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            await using (BoundedWriteStream bounded = new(file, ExtractionLimits.MaxResponseBytes))
            {
                await JsonSerializer.SerializeAsync(bounded, result, options).ConfigureAwait(false);
                await bounded.FlushAsync().ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            TryDelete(path);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    private sealed class BoundedWriteStream(Stream inner, long maxBytes) : Stream
    {
        private long bytesWritten;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => bytesWritten;
        public override long Position
        {
            get => bytesWritten;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureWithinLimit(count);
            inner.Write(buffer, offset, count);
            bytesWritten += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureWithinLimit(buffer.Length);
            inner.Write(buffer);
            bytesWritten += buffer.Length;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            EnsureWithinLimit(count);
            bytesWritten += count;
            return WriteInnerAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            EnsureWithinLimit(buffer.Length);
            bytesWritten += buffer.Length;
            return inner.WriteAsync(buffer, cancellationToken);
        }

        private async Task WriteInnerAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

        private void EnsureWithinLimit(int count)
        {
            if (count > maxBytes - bytesWritten)
                throw new ResponseSizeLimitExceededException();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
