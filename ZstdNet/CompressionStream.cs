using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using static ZstdNet.ExternMethods;

namespace ZstdNet
{
	public class CompressionStream : Stream
	{
		private readonly Stream _innerStream;
		private readonly byte[] _outputBuffer;
		private readonly int    _bufferSize;
#if !NETSTANDARD2_0
		private readonly ReadOnlyMemory<byte> _outputMemory;
#endif

		private nint  _cStream;
		private nuint _pos;

		private readonly bool _leaveOpen;

        public readonly CompressionOptions Options;

        public CompressionStream(Stream stream, bool leaveOpen = false)
            : this(stream, CompressionOptions.Default, 0, leaveOpen)
        { }

        public CompressionStream(Stream stream, int bufferSize, bool leaveOpen = false)
            : this(stream, CompressionOptions.Default, bufferSize, leaveOpen)
        { }

        public CompressionStream(Stream stream, CompressionOptions options, int bufferSize = 0, bool leaveOpen = false)
        {
#if NET6_0_OR_GREATER
            ReturnValueExtensions.ThrowIfDllNotExist();
#endif

            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (!stream.CanWrite)
                throw new ArgumentException("Stream is not writable", nameof(stream));
            if (bufferSize < 0)
                throw new ArgumentOutOfRangeException(nameof(bufferSize));

            _innerStream = stream;

            _cStream = ZSTD_createCStream().EnsureZstdSuccess();
            ZSTD_CCtx_reset(_cStream, ZSTD_ResetDirective.ZSTD_reset_session_only).EnsureZstdSuccess();

            Options = options;
            if (options != null)
            {
                options.ApplyCompressionParams(_cStream);

                if (options.Cdict != 0)
                    ZSTD_CCtx_refCDict(_cStream, options.Cdict).EnsureZstdSuccess();
            }

			this._bufferSize = bufferSize > 0 ? bufferSize : (int)ZSTD_CStreamOutSize().EnsureZstdSuccess();
			_outputBuffer = ArrayPool<byte>.Shared.Rent(this._bufferSize);
#if !NETSTANDARD2_0
			_outputMemory = new ReadOnlyMemory<byte>(_outputBuffer, 0, this._bufferSize);
#endif

	        this._leaveOpen = leaveOpen;
		}

#if !NETSTANDARD2_0
		public override void Write(ReadOnlySpan<byte> buffer)
		{
			EnsureNotDisposed();
            WriteInternal(_cStream, buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            EnsureNotDisposed();
            return WriteInternalAsync(_cStream, buffer, cancellationToken);
        }
#endif

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureParamsValid(buffer, offset, count);
            EnsureNotDisposed();
            WriteInternal(_cStream, new Span<byte>(buffer, offset, count));
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            EnsureParamsValid(buffer, offset, count);
            EnsureNotDisposed();

#if !NETSTANDARD2_0
			return WriteInternalAsync(_cStream, new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();
#else
            return WriteInternalAsync(_cStream, new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken);
#endif
		}

		private void WriteInternal(nint context, ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length == 0)
                return;

            ZSTD_Buffer input  = new(0, (nuint)buffer.Length);
            ZSTD_Buffer output = new(_pos, (nuint)_bufferSize);

            var outputSpan = new ReadOnlySpan<byte>(_outputBuffer, 0, _bufferSize);

            do
            {
                if (output.IsFullyConsumed)
                {
                    FlushOutputBuffer(outputSpan[..(int)output.pos]);
                    output.pos = 0;
                }

                Compress(context, buffer, ref output, ref input, ZSTD_EndDirective.ZSTD_e_continue);
            } while (!input.IsFullyConsumed);

            _pos = output.pos;
        }

		private async
#if !NETSTANDARD2_0
			ValueTask
#else
            Task
#endif
            WriteInternalAsync(nint context, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            if (buffer.Length == 0)
                return;

            ZSTD_Buffer input  = new(0, (nuint)buffer.Length);
            ZSTD_Buffer output = new(_pos, (nuint)_bufferSize);

            do
            {
                if (output.IsFullyConsumed)
                {
                    await FlushOutputBufferAsync(ref output, cancellationToken);
                    output.pos = 0;
                }

                Compress(context, buffer.Span, ref output, ref input, ZSTD_EndDirective.ZSTD_e_continue);
            } while (!input.IsFullyConsumed);

            _pos = output.pos;
        }

        private unsafe nuint Compress(nint context, ReadOnlySpan<byte> buffer, ref ZSTD_Buffer output, ref ZSTD_Buffer input, ZSTD_EndDirective directive)
		{
			input.buffer  = (nint)Unsafe.AsPointer(ref MemoryMarshal.GetReference(buffer));
			output.buffer = Marshal.UnsafeAddrOfPinnedArrayElement(_outputBuffer, 0);

			return ZSTD_compressStream2(context, ref output, ref input, directive).EnsureZstdSuccess();
        }

#if !NETSTANDARD2_0
		private void FlushOutputBuffer(ReadOnlySpan<byte> outputSpan)
			=> _innerStream.Write(outputSpan);
		private ValueTask FlushOutputBufferAsync(ref ZSTD_Buffer output, CancellationToken cancellationToken)
			=> _innerStream.WriteAsync(_outputMemory[..(int)output.pos], cancellationToken);
#else
        private void FlushOutputBuffer(ReadOnlySpan<byte> outputSpan)
            => _innerStream.Write(_outputBuffer, 0, outputSpan.Length);
        private Task FlushOutputBufferAsync(ref ZSTD_Buffer output, CancellationToken cancellationToken)
            => _innerStream.WriteAsync(_outputBuffer, 0, (int)output.pos, cancellationToken);
#endif

        ~CompressionStream() => Dispose(false);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            EnsureNotDisposed();
            FlushCompressStream(_cStream, ZSTD_EndDirective.ZSTD_e_flush);
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            EnsureNotDisposed();
#if !NETSTANDARD2_0
			return FlushCompressStreamAsync(_cStream, ZSTD_EndDirective.ZSTD_e_flush, cancellationToken).AsTask();
#else
            return FlushCompressStreamAsync(_cStream, ZSTD_EndDirective.ZSTD_e_flush, cancellationToken);
#endif
		}

		private void FlushCompressStream(nint context, ZSTD_EndDirective directive)
        {
            var buffer = ReadOnlySpan<byte>.Empty;

            ZSTD_Buffer input  = new(0, 0);
            ZSTD_Buffer output = new(_pos, (nuint)_bufferSize);

            var outputSpan = new ReadOnlySpan<byte>(_outputBuffer, 0, _bufferSize);

            do
            {
	            if (!output.IsFullyConsumed) continue;

	            FlushOutputBuffer(outputSpan[..(int)output.pos]);
	            output.pos = 0;
            } while (Compress(context, buffer, ref output, ref input, directive) != 0);

            if (output.pos != 0)
                FlushOutputBuffer(outputSpan[..(int)output.pos]);

            _pos = 0;
        }

		private async
#if !NETSTANDARD2_0
			ValueTask
#else
            Task
#endif
            FlushCompressStreamAsync(nint context, ZSTD_EndDirective directive, CancellationToken cancellationToken)
        {
            ZSTD_Buffer input  = new(0, 0);
            ZSTD_Buffer output = new(_pos, (nuint)_bufferSize);

            do
            {
                if (!output.IsFullyConsumed)
                    continue;

                await FlushOutputBufferAsync(ref output, cancellationToken);
                output.pos = 0;
            } while (Compress(context, ReadOnlySpan<byte>.Empty, ref output, ref input, directive) != 0);

            if (output.pos != 0)
                await FlushOutputBufferAsync(ref output, cancellationToken);

            _pos = 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

#if !NETSTANDARD2_0
		public override async ValueTask DisposeAsync()
		{
			await DisposeAsyncCore();
			GC.SuppressFinalize(this);
		}

        protected virtual async ValueTask DisposeAsyncCore()
		{
			nint cLastStream;
			if ((cLastStream = Interlocked.Exchange(ref _cStream, IntPtr.Zero)) == 0)
				return;

			try
            {
                await FlushCompressStreamAsync(cLastStream, ZSTD_EndDirective.ZSTD_e_end, CancellationToken.None);

                // Dispose if leaveOpen is false.
                if (!_leaveOpen)
	                await _innerStream.DisposeAsync();
			}
            finally
            {
                ZSTD_freeCStream(cLastStream);
                if (_outputBuffer != null)
                    ArrayPool<byte>.Shared.Return(_outputBuffer);
            }
        }
#endif

        protected override void Dispose(bool disposing)
		{
			if (!disposing)
				return;

			nint cLastStream;
			if ((cLastStream = Interlocked.Exchange(ref _cStream, IntPtr.Zero)) == 0)
				return;

			try
			{
				FlushCompressStream(cLastStream, ZSTD_EndDirective.ZSTD_e_end);

				// Dispose if leaveOpen is false.
				if (!_leaveOpen)
					_innerStream.Dispose();
			}
            finally
            {
                ZSTD_freeCStream(cLastStream);
                if (_outputBuffer != null)
                    ArrayPool<byte>.Shared.Return(_outputBuffer);
            }
        }

        private static void EnsureParamsValid(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (count > buffer.Length - offset)
                throw new ArgumentException("The sum of offset and count is greater than the buffer length");
        }

        private void EnsureNotDisposed()
        {
            if (_cStream == 0)
                throw new ObjectDisposedException(nameof(CompressionStream));
        }
    }
}
