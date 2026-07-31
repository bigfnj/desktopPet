using System;
using System.Threading;

namespace DesktopPet
{
    /// <summary>
    /// Owns the single current validation request. Starting a new request cancels
    /// every read and analysis operation associated with the previous one.
    /// </summary>
    internal sealed class ValidationLoadCoordinator : IDisposable
    {
        private readonly object sync = new object();
        private ValidationLoadSession current;
        private long generation;
        private bool disposed;

        public ValidationLoadSession Begin()
        {
            lock (sync)
            {
                ThrowIfDisposed();
                CancelCurrentLocked();
                generation++;
                current = new ValidationLoadSession(
                    generation,
                    new CancellationTokenSource());
                return current;
            }
        }

        public bool IsCurrent(ValidationLoadSession session)
        {
            if (session == null) return false;
            lock (sync)
            {
                return !disposed &&
                    ReferenceEquals(current, session) &&
                    !session.Token.IsCancellationRequested;
            }
        }

        public void Complete(ValidationLoadSession session)
        {
            if (session == null) return;
            lock (sync)
            {
                if (!ReferenceEquals(current, session)) return;
                current = null;
                session.Dispose();
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                generation++;
                CancelCurrentLocked();
            }
        }

        private void CancelCurrentLocked()
        {
            ValidationLoadSession previous = current;
            current = null;
            if (previous == null) return;
            previous.Cancel();
            previous.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
                throw new ObjectDisposedException("ValidationLoadCoordinator");
        }
    }

    internal sealed class ValidationLoadSession : IDisposable
    {
        private readonly CancellationTokenSource cancellation;
        private readonly CancellationToken token;

        internal ValidationLoadSession(
            long generation,
            CancellationTokenSource cancellation)
        {
            Generation = generation;
            this.cancellation = cancellation;
            token = cancellation.Token;
        }

        public long Generation { get; private set; }
        public CancellationToken Token { get { return token; } }

        internal void Cancel()
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            cancellation.Dispose();
        }
    }
}
