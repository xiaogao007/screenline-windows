using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Screenline
{
    public sealed class CaptureSources : IDisposable
    {
        public const string SelfClipboardFormat = "Screenline.InternalCopy";
        private readonly LibraryStore _library;
        private readonly Dispatcher _dispatcher;
        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private readonly Dictionary<string, PendingFile> _pending = new Dictionary<string, PendingFile>(StringComparer.OrdinalIgnoreCase);
        private readonly DispatcherTimer _timer;
        private AppSettings _settings;
        private bool _running;
        private bool _disposed;
        private uint _clipboardSequence;
        private bool _ignoreNextClipboard;
        private DateTime _ignoreExpires;
        private uint _pendingClipboardSequence;
        private int _clipboardRetries;
        private DateTime _lastWatcherRefresh;
        private int _watchGeneration;

        public CaptureSources(LibraryStore library, AppSettings settings, Dispatcher dispatcher)
        {
            if (library == null) throw new ArgumentNullException("library");
            if (settings == null) throw new ArgumentNullException("settings");
            if (dispatcher == null) throw new ArgumentNullException("dispatcher");
            _library = library;
            _settings = settings;
            _dispatcher = dispatcher;
            _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
            _timer.Interval = TimeSpan.FromMilliseconds(450);
            _timer.Tick += OnTick;
        }

        public event Action<string> Status;

        public void Start()
        {
            _dispatcher.VerifyAccess();
            if (_disposed) throw new ObjectDisposedException("CaptureSources");
            if (_running) return;
            _running = true;
            _clipboardSequence = GetClipboardSequenceNumber();
            _pendingClipboardSequence = 0;
            RefreshWatchers();
            _timer.Start();
        }

        public void Restart(AppSettings settings)
        {
            _dispatcher.VerifyAccess();
            if (settings == null) throw new ArgumentNullException("settings");
            if (_disposed) throw new ObjectDisposedException("CaptureSources");
            StopWatchers();
            _pending.Clear();
            _settings = settings;
            _clipboardSequence = GetClipboardSequenceNumber();
            _pendingClipboardSequence = 0;
            _ignoreNextClipboard = false;
            if (_running) RefreshWatchers();
        }

        // Call immediately before the application writes an image to the clipboard.
        // A private data-object marker can additionally use SelfClipboardFormat.
        public void IgnoreClipboardOnce()
        {
            _dispatcher.VerifyAccess();
            _clipboardSequence = GetClipboardSequenceNumber();
            _pendingClipboardSequence = 0;
            _ignoreNextClipboard = true;
            _ignoreExpires = DateTime.UtcNow.AddSeconds(2);
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (!_dispatcher.CheckAccess())
            {
                if (!_dispatcher.HasShutdownStarted) _dispatcher.Invoke(new Action(Dispose));
                return;
            }
            _disposed = true;
            _running = false;
            _timer.Stop();
            _timer.Tick -= OnTick;
            StopWatchers();
            _pending.Clear();
        }

        private void OnTick(object sender, EventArgs args)
        {
            if (!_running || _disposed) return;
            if (_settings.WatchClipboard) PollClipboard();
            ProcessPendingFiles();
            // A screenshot directory may be created after the first capture. Reattach
            // without enumerating/importing its existing contents.
            if (_settings.WatchFolders && DateTime.UtcNow - _lastWatcherRefresh > TimeSpan.FromSeconds(15))
                AttachMissingWatchers();
        }

        private void RefreshWatchers()
        {
            _lastWatcherRefresh = DateTime.UtcNow;
            if (!_settings.WatchFolders) return;
            AttachMissingWatchers();
        }

        private void AttachMissingWatchers()
        {
            _lastWatcherRefresh = DateTime.UtcNow;
            if (!_settings.WatchFolders || _settings.WatchPaths == null) return;
            foreach (string configured in _settings.WatchPaths)
            {
                if (String.IsNullOrWhiteSpace(configured)) continue;
                try
                {
                    string path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
                    if (!Directory.Exists(path) || IsManagedDirectory(path)) continue;
                    if (_watchers.Any(delegate(FileSystemWatcher watcher) { return String.Equals(watcher.Path, path, StringComparison.OrdinalIgnoreCase); })) continue;
                    FileSystemWatcher watcherNew = new FileSystemWatcher(path);
                    watcherNew.IncludeSubdirectories = false;
                    watcherNew.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;
                    watcherNew.InternalBufferSize = 32 * 1024;
                    watcherNew.Created += OnCreated;
                    watcherNew.Renamed += OnRenamed;
                    watcherNew.Changed += OnChanged;
                    watcherNew.Error += OnWatcherError;
                    try
                    {
                        watcherNew.EnableRaisingEvents = true;
                        _watchers.Add(watcherNew);
                    }
                    catch
                    {
                        watcherNew.Dispose();
                        throw;
                    }
                }
                catch (Exception ex)
                {
                    if (!StorageFiles.IsFileError(ex)) throw;
                    Tell("暂时无法监听截图目录：" + configured);
                }
            }
        }

        private bool IsManagedDirectory(string path)
        {
            string managed = _library.RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string target = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return String.Equals(target, managed, StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith(managed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private void StopWatchers()
        {
            _watchGeneration++;
            foreach (FileSystemWatcher watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Created -= OnCreated;
                watcher.Renamed -= OnRenamed;
                watcher.Changed -= OnChanged;
                watcher.Error -= OnWatcherError;
                watcher.Dispose();
            }
            _watchers.Clear();
        }

        private void OnCreated(object sender, FileSystemEventArgs args) { QueueFile(args.FullPath, false); }
        private void OnRenamed(object sender, RenamedEventArgs args) { QueueFile(args.FullPath, false); }
        private void OnChanged(object sender, FileSystemEventArgs args) { QueueFile(args.FullPath, true); }

        private void QueueFile(string path, bool onlyIfPending)
        {
            if (_disposed || !_running || !LibraryStore.IsSupportedFile(path)) return;
            int generation = _watchGeneration;
            Dispatch(delegate
            {
                if (_disposed || !_running || !_settings.WatchFolders || generation != _watchGeneration) return;
                string parent = Path.GetDirectoryName(path);
                if (!_watchers.Any(delegate(FileSystemWatcher watcher) { return String.Equals(watcher.Path, parent, StringComparison.OrdinalIgnoreCase); })) return;
                PendingFile pending;
                if (_pending.TryGetValue(path, out pending))
                {
                    pending.NextTry = DateTime.UtcNow.AddMilliseconds(600);
                    return;
                }
                if (onlyIfPending) return;
                _pending[path] = new PendingFile { Path = path, NextTry = DateTime.UtcNow.AddMilliseconds(600), FirstSeen = DateTime.UtcNow, LastLength = -1 };
            });
        }

        private void ProcessPendingFiles()
        {
            DateTime now = DateTime.UtcNow;
            foreach (PendingFile pending in _pending.Values.ToArray())
            {
                if (pending.NextTry > now) continue;
                bool retry = true;
                try
                {
                    FileInfo info = new FileInfo(pending.Path);
                    if (!info.Exists)
                    {
                        // A temp image may be renamed before the watcher is polled.
                        retry = now - pending.FirstSeen < TimeSpan.FromSeconds(3);
                    }
                    else if (info.Length > LibraryStore.MaximumFileBytes)
                    {
                        Tell("截图超过 100 MB，已跳过：" + info.Name);
                        retry = false;
                    }
                    else if (info.Length > 0 && info.Length == pending.LastLength && info.LastWriteTimeUtc == pending.LastWrite)
                    {
                        ShotItem imported = _library.ImportFile(pending.Path, true);
                        retry = imported == null;
                        if (imported != null) OnCaptured(imported);
                    }
                    pending.LastLength = info.Exists ? info.Length : -1;
                    pending.LastWrite = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
                }
                catch (Exception ex)
                {
                    if (!StorageFiles.IsFileError(ex)) throw;
                }
                pending.Attempts++;
                if (!retry || pending.Attempts >= 18 || now - pending.FirstSeen > TimeSpan.FromSeconds(35))
                {
                    _pending.Remove(pending.Path);
                    if (retry) Tell("截图尚未就绪，稍后可手动拖入：" + Path.GetFileName(pending.Path));
                }
                else pending.NextTry = now.AddMilliseconds(Math.Min(1800, 450 + pending.Attempts * 100));
            }
        }

        private void PollClipboard()
        {
            uint current = GetClipboardSequenceNumber();
            if (current != _clipboardSequence)
            {
                _clipboardSequence = current;
                _pendingClipboardSequence = 0;
                if (_ignoreNextClipboard && DateTime.UtcNow <= _ignoreExpires)
                {
                    _ignoreNextClipboard = false;
                    return;
                }
                _ignoreNextClipboard = false;
                _pendingClipboardSequence = current;
                _clipboardRetries = 0;
            }
            if (_pendingClipboardSequence == 0 || _pendingClipboardSequence != current) return;
            try
            {
                IDataObject data = Clipboard.GetDataObject();
                if (data == null || IsExcludedClipboard(data)) { _pendingClipboardSequence = 0; return; }
                if (!data.GetDataPresent(DataFormats.Bitmap, true)) { _pendingClipboardSequence = 0; return; }
                BitmapSource image = Clipboard.GetImage();
                if (image == null) { _pendingClipboardSequence = 0; return; }
                if (GetClipboardSequenceNumber() != current) return;
                if (image.CanFreeze) image.Freeze();
                _pendingClipboardSequence = 0;
                ShotItem imported = _library.ImportBitmap(image);
                if (imported != null) OnCaptured(imported);
            }
            catch (Exception ex)
            {
                if (!StorageFiles.IsFileError(ex) && !(ex is ExternalException)) throw;
                _clipboardRetries++;
                if (_clipboardRetries >= 8)
                {
                    _pendingClipboardSequence = 0;
                    Tell("剪贴板正忙，稍后可使用“粘贴图片”重试。");
                }
            }
        }

        private static bool IsExcludedClipboard(IDataObject data)
        {
            string[] excluded = new string[] { SelfClipboardFormat, "TendederoInternalCopy",
                "ExcludeClipboardContentFromMonitorProcessing", "DisableClipboardHistory",
                "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard",
                DataFormats.UnicodeText, DataFormats.Text, DataFormats.StringFormat, DataFormats.Html, DataFormats.FileDrop };
            foreach (string format in excluded)
                if (data.GetDataPresent(format, false)) return true;
            return false;
        }

        // Kept separate from Status so the host can reveal the shelf only when an
        // image was actually imported, and independently apply fullscreen rules.
        public event Action<ShotItem> Captured;
        private void OnCaptured(ShotItem item)
        {
            Action<ShotItem> handler = Captured;
            if (handler != null) handler(item);
        }

        private void OnWatcherError(object sender, ErrorEventArgs args)
        {
            Dispatch(delegate
            {
                if (_disposed || !_running) return;
                Tell("截图目录监听已重连；若刚才有遗漏，可手动拖入图片。");
                StopWatchers();
                RefreshWatchers();
            });
        }

        private void Dispatch(Action action)
        {
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
            try { _dispatcher.BeginInvoke(DispatcherPriority.Background, action); }
            catch (InvalidOperationException) { }
        }

        private void Tell(string message)
        {
            Action<string> handler = Status;
            if (handler != null) handler(message);
        }

        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();

        private sealed class PendingFile
        {
            internal string Path;
            internal DateTime FirstSeen;
            internal DateTime NextTry;
            internal long LastLength;
            internal DateTime LastWrite;
            internal int Attempts;
        }
    }
}
