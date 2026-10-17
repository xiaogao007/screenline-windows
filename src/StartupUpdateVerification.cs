using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Screenline
{
    // In-memory promises and an explicit UI queue exercise races without sockets,
    // timers, installers, windows, or writes to the user's settings or screenshot data.
    internal static class StartupUpdateVerification
    {
        internal static void Run(Action<bool, string> check)
        {
            Task.Run(delegate { return RunAsync(check); }).GetAwaiter().GetResult();
        }

        private static async Task RunAsync(Action<bool, string> verify)
        {
            ReleaseUpdate newer = NewerRelease();
            int requests = 0;
            int notifications = 0;
            using (var disabled = new StartupUpdateChecker(delegate
            {
                Interlocked.Increment(ref requests);
                return Task.FromResult(newer);
            }, delegate { Interlocked.Increment(ref notifications); }, null))
            {
                Task original = disabled.Start(false);
                await original.ConfigureAwait(false);
                disabled.SetEnabled(true);
                await disabled.Start(true).ConfigureAwait(false);
                verify(requests == 0 && notifications == 0 && Object.ReferenceEquals(original, disabled.Completion),
                    "Startup updates are opt-in and enabling them mid-session makes no request");
            }

            requests = 0; notifications = 0;
            var response = new TaskCompletionSource<ReleaseUpdate>();
            var entered = new TaskCompletionSource<int>();
            int startThread = Thread.CurrentThread.ManagedThreadId;
            ReleaseUpdate offered = null;
            using (var enabled = new StartupUpdateChecker(delegate
            {
                Interlocked.Increment(ref requests);
                entered.SetResult(Thread.CurrentThread.ManagedThreadId);
                return response.Task;
            }, delegate(ReleaseUpdate update) { notifications++; offered = update; }, null))
            {
                Task first = enabled.Start(true);
                Task second = enabled.Start(true);
                int checkThread = await entered.Task.ConfigureAwait(false);
                verify(!first.IsCompleted && checkThread != startThread,
                    "Startup check runs off the startup thread and returns while the response is pending");
                response.SetResult(newer);
                await first.ConfigureAwait(false);
                verify(requests == 1 && notifications == 1 && Object.ReferenceEquals(first, second) && Object.ReferenceEquals(offered, newer),
                    "An enabled startup session checks once and offers only its returned newer release");
            }

            notifications = 0;
            using (var current = new StartupUpdateChecker(delegate { return Task.FromResult<ReleaseUpdate>(null); },
                delegate { notifications++; }, null))
            {
                await current.Start(true).ConfigureAwait(false);
                verify(notifications == 0 && !current.Completion.IsFaulted,
                    "Equal, older, or absent releases returned as no update remain silent at startup");
            }
            notifications = 0;
            using (var failure = new StartupUpdateChecker(delegate { throw new InvalidOperationException("Offline fixture"); },
                delegate { notifications++; }, null))
            {
                await failure.Start(true).ConfigureAwait(false);
                verify(notifications == 0 && !failure.Completion.IsFaulted,
                    "A synchronous startup transport failure stays quiet and leaves no faulted background task");
            }
            var failedResponse = new TaskCompletionSource<ReleaseUpdate>();
            failedResponse.SetException(new UpdateException("Offline fixture"));
            using (var failure = new StartupUpdateChecker(delegate { return failedResponse.Task; },
                delegate { notifications++; }, null))
            {
                await failure.Start(true).ConfigureAwait(false);
                verify(notifications == 0 && !failure.Completion.IsFaulted,
                    "A failed asynchronous startup request stays quiet without an update prompt");
            }

            notifications = 0;
            response = new TaskCompletionSource<ReleaseUpdate>();
            var tokenEntered = new TaskCompletionSource<CancellationToken>();
            using (var canceled = new StartupUpdateChecker(delegate(CancellationToken token)
            {
                tokenEntered.SetResult(token);
                return response.Task; // Deliberately ignores cancellation.
            }, delegate { notifications++; }, null))
            {
                Task done = canceled.Start(true);
                CancellationToken token = await tokenEntered.Task.ConfigureAwait(false);
                canceled.SetEnabled(false);
                canceled.SetEnabled(true);
                response.SetResult(newer);
                await done.ConfigureAwait(false);
                verify(token.IsCancellationRequested && notifications == 0,
                    "Disabling cancels startup checking and suppresses a late response even if re-enabled");
            }

            notifications = 0;
            response = new TaskCompletionSource<ReleaseUpdate>();
            tokenEntered = new TaskCompletionSource<CancellationToken>();
            using (var exiting = new StartupUpdateChecker(delegate(CancellationToken token)
            {
                tokenEntered.SetResult(token);
                return response.Task;
            }, delegate { notifications++; }, null))
            {
                Task done = exiting.Start(true);
                CancellationToken token = await tokenEntered.Task.ConfigureAwait(false);
                exiting.Dispose();
                response.SetResult(newer);
                await done.ConfigureAwait(false);
                verify(token.IsCancellationRequested && notifications == 0,
                    "Application exit cancels startup checking and ignores a transport's late response");
            }

            var ui = new QueuedContext();
            notifications = 0;
            bool onCapturedContext = false;
            SynchronizationContext previous = SynchronizationContext.Current;
            StartupUpdateChecker captured;
            try
            {
                SynchronizationContext.SetSynchronizationContext(ui);
                captured = new StartupUpdateChecker(delegate { return Task.FromResult(newer); }, delegate
                {
                    notifications++;
                    onCapturedContext = Object.ReferenceEquals(SynchronizationContext.Current, ui);
                });
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            using (captured)
            {
                Task done = captured.Start(true);
                await ui.Posted.Task.ConfigureAwait(false);
                verify(notifications == 0 && !done.IsCompleted,
                    "Startup update notice is queued on the captured UI context before delivery");
                ui.Drain();
                await done.ConfigureAwait(false);
                verify(notifications == 1 && onCapturedContext,
                    "Startup update notice executes on the original UI context");
            }

            ui = new QueuedContext();
            notifications = 0;
            using (var queued = new StartupUpdateChecker(delegate { return Task.FromResult(newer); },
                delegate { notifications++; }, ui))
            {
                Task done = queued.Start(true);
                await ui.Posted.Task.ConfigureAwait(false);
                queued.SetEnabled(false);
                await done.ConfigureAwait(false);
                ui.Drain();
                verify(notifications == 0 && done.IsCompleted,
                    "Disabling also suppresses an already queued UI notice and completes without pumping the UI");
            }

            ui = new QueuedContext();
            notifications = 0;
            using (var queued = new StartupUpdateChecker(delegate { return Task.FromResult(newer); },
                delegate { notifications++; }, ui))
            {
                Task done = queued.Start(true);
                await ui.Posted.Task.ConfigureAwait(false);
                queued.Dispose();
                await done.ConfigureAwait(false);
                ui.Drain();
                verify(notifications == 0 && done.IsCompleted,
                    "Exit completes a pending UI delivery and suppresses its later callback");
            }

            requests = 0;
            using (var closed = new StartupUpdateChecker(delegate
            {
                requests++;
                return Task.FromResult(newer);
            }, delegate { }, null))
            {
                closed.Dispose();
                await closed.Start(true).ConfigureAwait(false);
                verify(requests == 0, "A disposed startup checker never starts a request");
            }

            using (var callbackFailure = new StartupUpdateChecker(delegate { return Task.FromResult(newer); },
                delegate { throw new InvalidOperationException("Closed UI fixture"); }, null))
            {
                await callbackFailure.Start(true).ConfigureAwait(false);
                verify(!callbackFailure.Completion.IsFaulted,
                    "A closed notification surface cannot fault the optional startup task");
            }
        }

        private static ReleaseUpdate NewerRelease()
        {
            const string assetBase = "https://github.com/xiaogao007/screenline-windows/releases/download/v9.0.0/";
            return new ReleaseUpdate("v9.0.0", new Uri("https://github.com/xiaogao007/screenline-windows/releases/tag/v9.0.0"),
                "Screenline-v9.0.0-windows-x64-setup.exe", 2,
                new Uri(assetBase + "Screenline-v9.0.0-windows-x64-setup.exe"), new Uri(assetBase + "SHA256SUMS.txt"), null);
        }

        private sealed class QueuedContext : SynchronizationContext
        {
            private readonly ConcurrentQueue<Action> callbacks = new ConcurrentQueue<Action>();
            internal readonly TaskCompletionSource<bool> Posted = new TaskCompletionSource<bool>();

            public override void Post(SendOrPostCallback callback, object state)
            {
                callbacks.Enqueue(delegate { callback(state); });
                Posted.TrySetResult(true);
            }

            internal void Drain()
            {
                SynchronizationContext previous = Current;
                try
                {
                    SetSynchronizationContext(this);
                    Action callback;
                    while (callbacks.TryDequeue(out callback)) callback();
                }
                finally { SetSynchronizationContext(previous); }
            }
        }
    }
}
