using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Screenline
{
    internal static class ServiceVerification
    {
        internal static void Run(string sandbox, Action<bool, string> check)
        {
            Directory.CreateDirectory(sandbox);
            string sources = Path.Combine(sandbox, "sources");
            string root = Path.Combine(sandbox, "library");
            Directory.CreateDirectory(sources);
            string sourceA = Path.Combine(sources, "first.png");
            string sourceB = Path.Combine(sources, "second.png");
            byte[] bytesA = PngBytes(25);
            byte[] bytesB = PngBytes(145);
            File.WriteAllBytes(sourceA, bytesA);
            File.WriteAllBytes(sourceB, bytesB);
            LibraryStore library = new LibraryStore(root);
            ShotItem first = library.ImportFile(sourceA);
            ShotItem second = library.ImportFile(sourceB);
            check(first != null && second != null && library.Items.Count == 2, "Import two actual PNG images into the managed library");
            if (first == null || second == null) return;
            check(first.Thumbnail != null && first.Thumbnail.IsFrozen && first.PixelWidth == 4 && first.PixelHeight == 3,
                "Thumbnail is decoded, frozen, and retains full image dimensions");
            bool unlocked = false;
            using (FileStream stream = new FileStream(first.FilePath, FileMode.Open, FileAccess.Read, FileShare.None)) unlocked = stream.Length > 0;
            check(unlocked, "Thumbnail does not keep the managed image file locked");
            string duplicatePath = Path.Combine(sources, "renamed-duplicate.png");
            File.WriteAllBytes(duplicatePath, bytesA);
            ShotItem duplicate = library.ImportFile(duplicatePath);
            check(Object.ReferenceEquals(first, duplicate) && library.Items.Count == 2, "SHA-256 deduplicates identical content with a different source name");
            check(library.Items[0].Id == second.Id, "Newest image is first");
            string corrupt = Path.Combine(sources, "broken.png");
            File.WriteAllText(corrupt, "This is not an image.");
            string unsupported = Path.Combine(sources, "document.txt");
            File.WriteAllText(unsupported, "Not a screenshot");
            check(library.ImportFile(corrupt) == null && library.ImportFile(unsupported) == null && library.Items.Count == 2,
                "Corrupt image content and unsupported extensions are rejected without list changes");

            check(library.Remove(first) && library.Items.Count == 1 && File.Exists(first.FilePath),
                "Removing an image retains the managed file and records trash");
            LibraryStore reloaded = new LibraryStore(root);
            reloaded.Load();
            check(reloaded.Items.Count == 1 && reloaded.TrashItems.Count == 1, "Active images and retained trash survive reload");
            check(reloaded.UndoLastRemove() && reloaded.Items.Count == 2, "Undo survives application reload");
            check(reloaded.RemoveAll() == 2 && reloaded.Items.Count == 0, "Clear shelf moves all entries into retained trash");
            LibraryStore emptyReload = new LibraryStore(root);
            emptyReload.Load();
            check(emptyReload.Items.Count == 0 && emptyReload.UndoLastRemove() && emptyReload.Items.Count == 2,
                "Clear and bulk Undo survive reload without losing images");
            check(File.Exists(sourceA) && File.Exists(sourceB) && EqualBytes(File.ReadAllBytes(sourceA), bytesA) &&
                EqualBytes(File.ReadAllBytes(sourceB), bytesB), "Original source images remain byte-for-byte unchanged");

            emptyReload.Save(); // Ensure the backup also contains both recovered images.
            File.WriteAllText(Path.Combine(root, "library.json"), "{ interrupted invalid index");
            bool recoveryReported = false;
            LibraryStore recovered = new LibraryStore(root);
            recovered.Status += delegate(string message) { if (message.IndexOf("备份", StringComparison.Ordinal) >= 0) recoveryReported = true; };
            recovered.Load();
            check(recovered.Items.Count == 2 && recoveryReported, "Corrupt primary manifest recovers the last atomic backup and reports recovery");
            recovered.Save();
            string blockedTemporary = Path.Combine(root, "library.json.tmp");
            Directory.CreateDirectory(blockedTemporary);
            bool rejectedRemove = !recovered.Remove(recovered.Items[0]);
            check(rejectedRemove && recovered.Items.Count == 2 && recovered.TrashItems.Count == 0,
                "A failed atomic manifest write rolls back the list mutation");
            Directory.Delete(blockedTemporary);

            AppSettings defaults = new AppSettings();
            check(!defaults.WatchClipboard && defaults.WatchFolders && defaults.RevealOnCapture && defaults.HideInFullscreen,
                "Clipboard watching is opt-in; screenshot folders and fullscreen protection default on");
            check(!defaults.CheckUpdatesOnStartup, "Startup update checks require explicit opt-in by default");
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            bool desktopExcluded = true;
            foreach (string watchPath in defaults.WatchPaths)
                if (String.Equals(watchPath.TrimEnd('\\'), desktop.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) desktopExcluded = false;
            check(desktopExcluded, "Default monitoring paths do not include the desktop");
            defaults.WatchClipboard = true;
            defaults.CheckUpdatesOnStartup = true;
            defaults.WatchFolders = false;
            defaults.WatchPaths = new string[] { sources };
            SettingsStore.Save(root, defaults);
            AppSettings settingsReloaded = SettingsStore.Load(root);
            check(settingsReloaded.WatchClipboard && settingsReloaded.CheckUpdatesOnStartup && !settingsReloaded.WatchFolders && settingsReloaded.WatchPaths.Length == 1 &&
                settingsReloaded.WatchPaths[0] == sources, "Settings and explicit clipboard and startup update consent persist");
            defaults.WatchClipboard = false;
            defaults.CheckUpdatesOnStartup = false;
            SettingsStore.Save(root, defaults);
            check(!SettingsStore.Load(root).CheckUpdatesOnStartup, "Disabling startup update checks persists across settings reload");
            File.WriteAllText(Path.Combine(root, "settings.json"), "{ broken settings");
            AppSettings settingsRecovered = SettingsStore.Load(root);
            check(!settingsRecovered.WatchClipboard && !settingsRecovered.CheckUpdatesOnStartup && settingsRecovered.WatchPaths.Length == 1 && settingsRecovered.WatchPaths[0] == sources,
                "Corrupt settings recover backup paths while disabling clipboard and startup update checks until new consent");
            string legacyRoot = Path.Combine(sandbox, "legacy-settings");
            Directory.CreateDirectory(legacyRoot);
            File.WriteAllText(Path.Combine(legacyRoot, "settings.json"), "{\"WatchClipboard\":false,\"WatchFolders\":true,\"HideInFullscreen\":true,\"RevealOnCapture\":true}");
            AppSettings legacySettings = SettingsStore.Load(legacyRoot);
            check(!legacySettings.CheckUpdatesOnStartup && legacySettings.WatchFolders && legacySettings.WatchPaths != null,
                "Existing settings without a startup update field migrate with update checks disabled");
            string temporaryRoot = Path.Combine(sandbox, "temporary-settings");
            SettingsStore.Save(temporaryRoot, new AppSettings { WatchClipboard = true, CheckUpdatesOnStartup = true, WatchPaths = new string[] { sources } });
            File.Move(Path.Combine(temporaryRoot, "settings.json"), Path.Combine(temporaryRoot, "settings.json.tmp"));
            AppSettings temporaryRecovered = SettingsStore.Load(temporaryRoot);
            check(!temporaryRecovered.WatchClipboard && !temporaryRecovered.CheckUpdatesOnStartup && temporaryRecovered.WatchPaths.Length == 1 && temporaryRecovered.WatchPaths[0] == sources,
                "Temporary settings recovery keeps paths but requires fresh clipboard and startup update consent");

            VerifyClipboardPrivacy(check);
            VerifyFolderWatcher(sandbox, bytesA, bytesB, check);
        }

        private static void VerifyClipboardPrivacy(Action<bool, string> check)
        {
            MethodInfo excluded = typeof(CaptureSources).GetMethod("IsExcludedClipboard", BindingFlags.NonPublic | BindingFlags.Static);
            string[] privateFormats = new string[] { CaptureSources.SelfClipboardFormat, "ExcludeClipboardContentFromMonitorProcessing",
                "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard", DataFormats.UnicodeText, DataFormats.Html, DataFormats.FileDrop };
            bool allExcluded = true;
            foreach (string format in privateFormats)
            {
                DataObject data = new DataObject();
                data.SetData(format, new byte[] { 0, 0, 0, 0 }, false);
                if (!(bool)excluded.Invoke(null, new object[] { data })) allExcluded = false;
            }
            check(allExcluded, "Synthetic clipboard data excludes private, text, file-drop, and own-copy formats without reading the system clipboard");
        }

        private static void VerifyFolderWatcher(string sandbox, byte[] bytesA, byte[] bytesB, Action<bool, string> check)
        {
            string watched = Path.Combine(sandbox, "watch-folder");
            Directory.CreateDirectory(watched);
            File.WriteAllBytes(Path.Combine(watched, "existing.png"), bytesA);
            LibraryStore library = new LibraryStore(Path.Combine(sandbox, "watch-library"));
            AppSettings settings = new AppSettings { WatchClipboard = false, WatchFolders = true, WatchPaths = new string[] { watched } };
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            using (CaptureSources capture = new CaptureSources(library, settings, dispatcher))
            {
                capture.Start();
                Pump(150);
                check(library.Items.Count == 0, "Starting folder monitoring does not import pre-existing screenshots");
                string partial = Path.Combine(watched, "partial.png");
                using (FileStream stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytesA, 0, bytesA.Length / 2);
                    stream.Flush(true);
                    Pump(850);
                    check(library.Items.Count == 0, "Created screenshot remains pending while a partial file is locked for writing");
                    stream.Write(bytesA, bytesA.Length / 2, bytesA.Length - bytesA.Length / 2);
                    stream.Flush(true);
                }
                PumpUntil(delegate { return library.Items.Count == 1; }, 4000);
                check(library.Items.Count == 1, "Folder watcher imports a new image after its write finishes");
                string temporary = Path.Combine(watched, "rename.part");
                File.WriteAllBytes(temporary, bytesB);
                File.Move(temporary, Path.Combine(watched, "renamed.png"));
                PumpUntil(delegate { return library.Items.Count == 2; }, 4000);
                check(library.Items.Count == 2, "Folder watcher imports a completed image renamed to a supported extension");
            }
        }

        private static byte[] PngBytes(byte seed)
        {
            byte[] pixels = new byte[4 * 3 * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = seed;
                pixels[i + 1] = (byte)(seed + i);
                pixels[i + 2] = (byte)(255 - seed);
                pixels[i + 3] = 255;
            }
            BitmapSource bitmap = BitmapSource.Create(4, 3, 96, 96, PixelFormats.Bgra32, null, pixels, 16);
            bitmap.Freeze();
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (MemoryStream stream = new MemoryStream()) { encoder.Save(stream); return stream.ToArray(); }
        }

        private static bool EqualBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static void Pump(int milliseconds)
        {
            DispatcherFrame frame = new DispatcherFrame();
            DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        private static void PumpUntil(Func<bool> condition, int timeoutMilliseconds)
        {
            if (condition()) return;
            Stopwatch elapsed = Stopwatch.StartNew();
            DispatcherFrame frame = new DispatcherFrame();
            DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
            timer.Tick += delegate
            {
                if (!condition() && elapsed.ElapsedMilliseconds < timeoutMilliseconds) return;
                timer.Stop();
                frame.Continue = false;
            };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }
}
