using System;
using System.Threading;
using System.Threading.Tasks;

namespace Screenline
{
    // A session owns one optional background check. Enabling the setting while the
    // app is running applies to its next start; only an explicit UI action downloads.
    internal sealed class StartupUpdateChecker : IDisposable
    {
        private readonly object gate = new object();
        private readonly Func<CancellationToken, Task<ReleaseUpdate>> check;
        private readonly Action<ReleaseUpdate> notify;
        private readonly SynchronizationContext notificationContext;
        private CancellationTokenSource cancellation;
        private Task completion = Task.FromResult(0);
        private bool started;
        private bool enabled;
        private bool disposed;

        internal StartupUpdateChecker(Func<CancellationToken, Task<ReleaseUpdate>> check, Action<ReleaseUpdate> notify)
            : this(check, notify, SynchronizationContext.Current)
        {
        }

        internal StartupUpdateChecker(Func<CancellationToken, Task<ReleaseUpdate>> check,
            Action<ReleaseUpdate> notify, SynchronizationContext notificationContext)
        {
            if (check == null) throw new ArgumentNullException("check");
            if (notify == null) throw new ArgumentNullException("notify");
            this.check = check;
            this.notify = notify;
            this.notificationContext = notificationContext;
        }

        internal Task Completion { get { lock (gate) return completion; } }

        internal Task Start(bool startupEnabled)
        {
            lock (gate)
            {
                if (started) return completion;
                started = true;
                enabled = startupEnabled && !disposed;
                if (!enabled) return completion;
                cancellation = new CancellationTokenSource();
                CancellationTokenSource source = cancellation;
                // Even a transport with synchronous preparation must not delay WPF startup.
                completion = Task.Run(delegate { return RunAsync(source); });
                return completion;
            }
        }

        internal void SetEnabled(bool value)
        {
            CancellationTokenSource source;
            lock (gate)
            {
                enabled = value && !disposed;
                source = !enabled ? cancellation : null;
            }
            // A canceled check cannot be resumed by switching the setting back on.
            CancelQuietly(source);
        }

        public void Dispose()
        {
            CancellationTokenSource source;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                enabled = false;
                source = cancellation;
            }
            CancelQuietly(source);
        }

        private async Task RunAsync(CancellationTokenSource source)
        {
            CancellationToken token = source.Token;
            try
            {
                token.ThrowIfCancellationRequested();
                ReleaseUpdate update = await check(token).ConfigureAwait(false);
                if (update == null || token.IsCancellationRequested) return;
                if (notificationContext == null)
                {
                    NotifyIfActive(update, token);
                    return;
                }
                var delivered = new TaskCompletionSource<bool>();
                // Cancellation completes our lifetime even if a shutting-down UI never
                // runs the posted callback. That callback still checks the token later.
                using (token.Register(delegate { delivered.TrySetResult(false); }))
                {
                    notificationContext.Post(delegate
                    {
                        try { NotifyIfActive(update, token); }
                        finally { delivered.TrySetResult(true); }
                    }, null);
                    await delivered.Task.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { } // An optional startup check never interrupts normal use.
            finally { source.Dispose(); }
        }

        private void NotifyIfActive(ReleaseUpdate update, CancellationToken token)
        {
            lock (gate)
            {
                if (!enabled || disposed || token.IsCancellationRequested) return;
                try { notify(update); }
                catch (Exception) { } // No unhandled dispatcher exception on app shutdown.
            }
        }

        private static void CancelQuietly(CancellationTokenSource source)
        {
            if (source == null) return;
            try { source.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (AggregateException) { }
        }
    }
}
