using System.Diagnostics;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using ModularMonolith.Platform.Transport;

namespace ModularMonolith.Tests;

internal sealed class TrackedWorkerTransport(IWorkerTransport inner) : IWorkerTransport
{
    private readonly object _gate = new();
    private TaskCompletionSource? _writeObserved;
    private TaskCompletionSource? _releaseWrite;
    private int _opened;
    private int _closed;

    public string Name => inner.Name;
    public int Opened => Volatile.Read(ref _opened);
    public int Active => Opened - Volatile.Read(ref _closed);

    /// <summary>Holds the next request write after its bytes reach the transport.</summary>
    public Task HoldNextWrite()
    {
        lock (_gate)
        {
            if (_releaseWrite is not null)
            {
                throw new InvalidOperationException("A request write is already held.");
            }

            _writeObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _releaseWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _writeObserved.Task;
        }
    }

    /// <summary>Releases the request write held by the test.</summary>
    public void ReleaseWrite()
    {
        lock (_gate)
        {
            _releaseWrite?.TrySetResult();
            _releaseWrite = null;
            _writeObserved = null;
        }
    }

    /// <summary>Waits until all opened connections have been disposed.</summary>
    public async Task WaitForNoConnectionsAsync(TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (Active != 0 && stopwatch.Elapsed < timeout)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        if (Active != 0)
        {
            throw new TimeoutException($"{Active} of {Opened} tracked connections remain open.");
        }
    }

    /// <summary>Creates the underlying endpoint allocator.</summary>
    public IEndpointAllocator CreateAllocator(IConfiguration configuration) => inner.CreateAllocator(configuration);

    /// <summary>Wraps the real transport stream to track its lifetime and request writes.</summary>
    public HttpMessageHandler CreateClientHandler(WorkerEndpoint endpoint)
    {
        if (inner.CreateClientHandler(endpoint) is not SocketsHttpHandler handler || handler.ConnectCallback is null)
        {
            throw new InvalidOperationException("The transport must use a SocketsHttpHandler with a connect callback.");
        }

        var connect = handler.ConnectCallback;
        handler.ConnectCallback = async (context, cancellationToken) =>
        {
            var stream = await connect(context, cancellationToken);
            Interlocked.Increment(ref _opened);
            return new TrackedStream(stream, this);
        };
        return handler;
    }

    /// <summary>Configures the underlying transport listener.</summary>
    public void Listen(KestrelServerOptions options, WorkerEndpoint endpoint) => inner.Listen(options, endpoint);

    /// <summary>Cleans up the underlying endpoint.</summary>
    public void Cleanup(WorkerEndpoint endpoint) => inner.Cleanup(endpoint);

    private Task? AfterWrite()
    {
        lock (_gate)
        {
            if (_writeObserved is null || _releaseWrite is null)
            {
                return null;
            }

            _writeObserved.TrySetResult();
            _writeObserved = null;
            return _releaseWrite.Task;
        }
    }

    private sealed class TrackedStream(Stream inner, TrackedWorkerTransport owner) : Stream
    {
        private int _disposed;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        /// <summary>Flushes the underlying stream.</summary>
        public override void Flush() => inner.Flush();

        /// <summary>Flushes the underlying stream asynchronously.</summary>
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        /// <summary>Reads bytes from the underlying stream.</summary>
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        /// <summary>Reads bytes asynchronously from the underlying stream.</summary>
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);

        /// <summary>Reads bytes asynchronously from the underlying stream.</summary>
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        /// <summary>Seeks in the underlying stream.</summary>
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        /// <summary>Sets the underlying stream length.</summary>
        public override void SetLength(long value) => inner.SetLength(value);

        /// <summary>Writes bytes to the underlying stream.</summary>
        public override void Write(byte[] buffer, int offset, int count)
            => inner.Write(buffer, offset, count);

        /// <summary>Writes bytes asynchronously, observing a test-controlled request gate.</summary>
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            var gate = owner.AfterWrite();
            if (gate is not null)
            {
                await gate.WaitAsync(cancellationToken);
            }
        }

        /// <summary>Writes bytes asynchronously to the underlying stream.</summary>
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    inner.Dispose();
                }
                finally
                {
                    Interlocked.Increment(ref owner._closed);
                }
            }

            base.Dispose(disposing);
        }

        /// <summary>Disposes the underlying stream and records the closed connection.</summary>
        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    await inner.DisposeAsync();
                }
                finally
                {
                    Interlocked.Increment(ref owner._closed);
                }
            }

            GC.SuppressFinalize(this);
        }
    }
}
