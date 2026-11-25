using System.Text.Json;
using System.Text.Json.Serialization;
using RvtIO.Model;

internal static class FullJsonOutput
{
    private const int Batch = 64;
    private const int BufferLimit = 2 << 20;

    internal static void Write(FileStream output, RevitFileInfo model, JsonSerializerOptions options)
    {
        int workers = Math.Min(8, Environment.ProcessorCount);
        if (model.Elements.Count < 4096 || workers < 2)
        {
            JsonSerializer.Serialize(output, model, options);
            return;
        }

        var rootOptions = new JsonSerializerOptions(options);
        rootOptions.Converters.Insert(0, new Elements(output, options, workers));
        using var writer = new Utf8JsonWriter(new FileBuffer(output), new JsonWriterOptions { Indented = options.WriteIndented, Encoder = options.Encoder });
        JsonSerializer.Serialize(writer, model, rootOptions);
        writer.Flush();
        output.SetLength(output.Position);
    }

    // Metadata and embedded family storage outside Elements must stream as well. An externally
    // supplied Utf8JsonWriter over a Stream would retain the bytes between explicit flushes.
    private sealed class FileBuffer(FileStream output) : System.Buffers.IBufferWriter<byte>
    {
        private byte[] _buffer = new byte[64 << 10];
        public void Advance(int count) => output.Write(_buffer, 0, count);
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (sizeHint > _buffer.Length) _buffer = new byte[sizeHint];
            return _buffer;
        }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }

    // Each batch is serialized at depth two, just like the Elements property of the model.
    // The serializer supplies the envelope, including the runtime's exact indentation and newline.
    private sealed class Elements(FileStream output, JsonSerializerOptions options, int workers) : JsonConverter<List<Element>>
    {
        public override List<Element> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions opts) => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, List<Element> elements, JsonSerializerOptions opts)
        {
            byte[] sample = JsonSerializer.SerializeToUtf8Bytes(new[] { new[] { 0 } }, options);
            int prefix = Array.LastIndexOf(sample, (byte)'[') + 1;
            int afterValue = Array.IndexOf(sample, (byte)'0') + 1;
            int suffix = sample.Length - afterValue;
            int indent = Array.IndexOf(sample, (byte)']') - afterValue;
            writer.WriteStartArray();
            writer.Flush();
            var buffers = new BatchBuffer[workers];
            var pending = new Task[workers];
            for (int i = 0; i < workers; i++)
            {
                buffers[i] = new();
                pending[i] = Start(i, i * Batch);
            }
            try
            {
                for (int offset = 0, slot = 0; offset < elements.Count; slot = (slot + 1) % workers)
                {
                    pending[slot].GetAwaiter().GetResult();
                    if (offset != 0) output.WriteByte((byte)',');
                    var buffer = buffers[slot];
                    if (buffer.Length != 0)
                        output.Write(buffer.GetBuffer(), prefix, (int)buffer.Length - prefix - suffix);
                    else
                    {
                        // A single element can be larger than the entire worker budget. Use the
                        // serializer's normal streaming path and overwrite the trailing array delimiters.
                        SerializeBatch(new SkipPrefix(output, prefix), offset);
                        output.Position -= suffix;
                    }
                    if (elements.Count - offset > workers * Batch)
                        pending[slot] = Start(slot, offset + workers * Batch);
                    offset += Math.Min(Batch, elements.Count - offset);
                }
            }
            finally
            {
                // Workers never write to the file. Join them on an I/O or serialization
                // failure without replacing the first exception in output order.
                Task.WhenAll(pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing).GetAwaiter().GetResult();
            }
            if (elements.Count > 0) output.Write(sample, afterValue, indent);
            writer.WriteEndArray();

            Task Start(int slot, int start) => start >= elements.Count ? Task.CompletedTask : Task.Run(() =>
            {
                var buffer = buffers[slot];
                buffer.SetLength(0);
                try { SerializeBatch(buffer, start); }
                catch (BatchTooLarge) { buffer.SetLength(0); }
            });

            void SerializeBatch(Stream stream, int start) =>
                JsonSerializer.Serialize(stream, new[] { elements.GetRange(start, Math.Min(Batch, elements.Count - start)) }, options);
        }
    }

    private sealed class BatchTooLarge : Exception;

    private sealed class BatchBuffer() : MemoryStream(new byte[BufferLimit], 0, BufferLimit, writable: true, publiclyVisible: true)
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Position + count > BufferLimit) throw new BatchTooLarge();
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Position + buffer.Length > BufferLimit) throw new BatchTooLarge();
            base.Write(buffer);
        }
    }

    private sealed class SkipPrefix(Stream target, int skip) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            int ignored = Math.Min(skip, buffer.Length);
            skip -= ignored;
            target.Write(buffer[ignored..]);
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() => target.Flush();
    }
}
