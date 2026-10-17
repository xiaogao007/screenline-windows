using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Screenline
{
    // This suite changes the real Windows clipboard. It is deliberately separate
    // from --self-test, and is only invoked explicitly by CI or a developer.
    internal static class ClipboardVerification
    {
        private const int Width = 17;
        private const int Height = 11;
        private const uint DibFormat = 8;
        private const uint FileDropFormat = 15;
        private const uint UnicodeTextFormat = 13;
        private const string ReplacementText = "Screenline clipboard contention test replacement";
        private static readonly List<string> lines = new List<string>();
        private static int failures;

        internal static int Run(string destination)
        {
            destination = Path.GetFullPath(destination);
            Directory.CreateDirectory(destination);
            lines.Clear(); failures = 0;
            lines.Add("Screenline opt-in real Windows clipboard integration test");
            lines.Add("OS: " + Environment.OSVersion + "; CLR: " + Environment.Version);
            string sandbox = Path.Combine(destination, "clipboard-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sandbox);
            string fixture = Path.Combine(sandbox, "\u4e2d\u6587 original odd-width fixture.png");
            try
            {
                RunWithDispatcher(async delegate(IntPtr owner)
                {
                    BitmapSource image = CreateFixture();
                    SaveFixture(image, fixture);
                    await Scenario("Persistent image copy", async delegate
                    {
                        // A separate process writes, then exits before this process
                        // reads. This catches accidental delayed-render ownership.
                        using (Process writer = StartHelper("--write-clipboard-fixture", fixture))
                        {
                            await WaitForExit(writer, 5000);
                            Check(writer.ExitCode == 0, "Writer process exits successfully");
                        }
                        VerifyContents(owner, fixture, image);
                    });
                    await Scenario("Temporary clipboard contention", async delegate
                    {
                        using (ClipboardLock blocker = await ClipboardLock.Start(sandbox))
                        {
                            int heartbeat = 0;
                            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                            timer.Tick += delegate { heartbeat++; };
                            timer.Start();
                            try
                            {
                                Task copy = ImageClipboardWriter.CopyAsync(image, fixture, owner, CancellationToken.None);
                                await Task.Delay(240);
                                Check(!copy.IsCompleted, "Copy waits while another process holds OpenClipboard");
                                await blocker.Release("release");
                                await copy;
                                Check(heartbeat >= 3, "Dispatcher remains responsive during clipboard retries");
                            }
                            finally { timer.Stop(); }
                        }
                        VerifyContents(owner, fixture, image);
                    });
                    await Scenario("Persistent clipboard contention", async delegate
                    {
                        using (ClipboardLock blocker = await ClipboardLock.Start(sandbox))
                        {
                            bool refused = false;
                            var clock = Stopwatch.StartNew();
                            try { await ImageClipboardWriter.CopyAsync(image, fixture, owner, CancellationToken.None); }
                            catch (ClipboardBusyException) { refused = true; }
                            clock.Stop();
                            Check(refused, "Persistent clipboard lock returns ClipboardBusyException");
                            Check(clock.ElapsedMilliseconds < 3500, "Persistent lock has a bounded retry budget");
                            Check(!blocker.HasExited, "Busy failure occurs before external lock is released");
                        }
                    });
                    await Scenario("Cancellation while clipboard is busy", async delegate
                    {
                        using (ClipboardLock blocker = await ClipboardLock.Start(sandbox))
                        using (var cancellation = new CancellationTokenSource())
                        {
                            cancellation.CancelAfter(100);
                            bool cancelled = false;
                            var clock = Stopwatch.StartNew();
                            try { await ImageClipboardWriter.CopyAsync(image, fixture, owner, cancellation.Token); }
                            catch (OperationCanceledException) { cancelled = true; }
                            Check(cancelled && clock.ElapsedMilliseconds < 1500, "Cancellation stops retrying promptly");
                            Check(!blocker.HasExited, "Cancellation completes while external clipboard lock remains held");
                        }
                    });
                    await Scenario("New clipboard content during a retry", async delegate
                    {
                        using (ClipboardLock blocker = await ClipboardLock.Start(sandbox))
                        {
                            Task copy = ImageClipboardWriter.CopyAsync(image, fixture, owner, CancellationToken.None);
                            await Task.Delay(180);
                            await blocker.Release("replace");
                            bool superseded = false;
                            try { await copy; }
                            catch (InvalidOperationException) { superseded = true; }
                            Check(superseded, "Copy refuses to overwrite clipboard content changed during retries");
                            Check(Clipboard.GetText() == ReplacementText, "New external clipboard content is preserved");
                        }
                    });
                    await Scenario("Recovery after failed and cancelled copies", async delegate
                    {
                        await ImageClipboardWriter.CopyAsync(image, fixture, owner, CancellationToken.None);
                        VerifyContents(owner, fixture, image);
                    });
                });
            }
            catch (Exception ex) { Check(false, "Unexpected integration exception: " + ex); }
            lines.Add("CHECKS: " + (lines.FindAll(delegate(string value) { return value.StartsWith("PASS ", StringComparison.Ordinal); }).Count) + " passed");
            lines.Add("RESULT: " + (failures == 0 ? "PASS" : "FAIL") + "; failures=" + failures);
            File.WriteAllLines(Path.Combine(destination, "report.txt"), lines.ToArray(), Encoding.UTF8);
            return failures == 0 ? 0 : 1;
        }

        // Child-process entry point. Native eager clipboard data must outlive us.
        internal static int WriteFixture(string filePath)
        {
            try
            {
                RunWithDispatcher(async delegate(IntPtr owner)
                {
                    BitmapSource fixture = CreateFixture();
                    SaveFixture(fixture, filePath);
                    await ImageClipboardWriter.CopyAsync(fixture, filePath, owner, CancellationToken.None);
                });
                return 0;
            }
            catch { return 1; }
        }

        // A real independent process owns the lock, without clearing or changing
        // the clipboard. It always releases within ten seconds if its parent dies.
        internal static int Hold(string readyPath, string releasePath)
        {
            bool opened = false;
            try
            {
                using (HwndSource window = CreateOwner())
                {
                    var clock = Stopwatch.StartNew();
                    while (!(opened = OpenClipboard(window.Handle)) && clock.ElapsedMilliseconds < 3000) Thread.Sleep(10);
                    if (!opened) return 2;
                    File.WriteAllText(readyPath, "ready");
                    while (!File.Exists(releasePath) && clock.ElapsedMilliseconds < 10000) Thread.Sleep(10);
                    if (File.Exists(releasePath) && File.ReadAllText(releasePath) == "replace") ReplaceWithText();
                    CloseClipboard(); opened = false;
                }
                return 0;
            }
            catch { return 3; }
            finally { if (opened) CloseClipboard(); }
        }

        private static async Task Scenario(string name, Func<Task> test)
        {
            try { await test(); }
            catch (Exception ex) { Check(false, name + ": " + ex); }
        }

        private static void RunWithDispatcher(Func<IntPtr, Task> operation)
        {
            Exception failure = null;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            using (HwndSource owner = CreateOwner())
            {
                app.Dispatcher.BeginInvoke(new Action(async delegate
                {
                    try { await operation(owner.Handle); }
                    catch (Exception ex) { failure = ex; }
                    finally { app.Shutdown(); }
                }));
                app.Run();
            }
            if (failure != null) throw new InvalidOperationException("Clipboard integration operation failed", failure);
        }

        private static HwndSource CreateOwner()
        {
            return new HwndSource(new HwndSourceParameters("Screenline clipboard test owner") { Width = 1, Height = 1, WindowStyle = 0 });
        }

        private static BitmapSource CreateFixture()
        {
            byte[] pixels = new byte[Width * Height * 4];
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int offset = (y * Width + x) * 4;
                pixels[offset] = (byte)(20 + x * 11);
                pixels[offset + 1] = (byte)(30 + y * 17);
                pixels[offset + 2] = (byte)(210 - x * 7 - y * 3);
                pixels[offset + 3] = (byte)(x > 0 && x < Width - 1 && y > 0 && y < Height - 1 && (x + y) % 3 == 0 ? 96 : 255);
            }
            BitmapSource source = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, pixels, Width * 4);
            source.Freeze();
            return source;
        }

        private static void SaveFixture(BitmapSource image, string filePath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath)));
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (FileStream stream = File.Create(filePath)) encoder.Save(stream);
        }

        private static void VerifyContents(IntPtr owner, string filePath, BitmapSource expected)
        {
            byte[] png = null, dib = null;
            string droppedFile = null;
            bool marker = false;
            bool opened = false;
            try
            {
                for (int attempt = 0; attempt < 30 && !(opened = OpenClipboard(owner)); attempt++) Thread.Sleep(10);
                if (!opened) throw new InvalidOperationException("Could not open clipboard to inspect copied formats");
                png = ReadGlobal(GetClipboardData(RegisterClipboardFormat("PNG")));
                dib = ReadGlobal(GetClipboardData(DibFormat));
                marker = IsClipboardFormatAvailable(RegisterClipboardFormat(CaptureSources.SelfClipboardFormat));
                IntPtr files = GetClipboardData(FileDropFormat);
                if (files != IntPtr.Zero && DragQueryFile(files, UInt32.MaxValue, null, 0) == 1)
                {
                    uint length = DragQueryFile(files, 0, null, 0);
                    var path = new StringBuilder((int)length + 1);
                    DragQueryFile(files, 0, path, path.Capacity);
                    droppedFile = path.ToString();
                }
            }
            finally { if (opened) CloseClipboard(); }
            Check(String.Equals(droppedFile, filePath, StringComparison.OrdinalIgnoreCase), "File-drop format references original full-resolution PNG path");
            Check(marker, "Internal-copy marker is available to prevent capture loops");
            Check(dib != null && dib.Length >= 40 && BitConverter.ToInt32(dib, 4) == Width && Math.Abs(BitConverter.ToInt32(dib, 8)) == Height,
                "Native DIB retains original odd-width image dimensions");
            BitmapSource pasted = Clipboard.GetImage();
            Check(pasted != null && pasted.PixelWidth == Width && pasted.PixelHeight == Height, "Windows image reader can paste original image dimensions");
            Check(pasted != null && MatchPixels(expected, pasted, true), "Windows image reader preserves opaque pixel colors and orientation");
            Check(png != null, "Native PNG format is available");
            if (png != null)
            {
                using (var stream = new MemoryStream(png, false))
                {
                    BitmapSource decoded = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                    Check(MatchPixels(expected, decoded, false), "PNG format preserves every color and alpha pixel");
                }
            }
        }

        private static bool MatchPixels(BitmapSource expected, BitmapSource actual, bool opaqueOnly)
        {
            if (actual.PixelWidth != Width || actual.PixelHeight != Height) return false;
            var converted = new FormatConvertedBitmap(actual, PixelFormats.Bgra32, null, 0);
            byte[] wanted = new byte[Width * Height * 4], found = new byte[Width * Height * 4];
            expected.CopyPixels(wanted, Width * 4, 0); converted.CopyPixels(found, Width * 4, 0);
            for (int offset = 0; offset < wanted.Length; offset += 4)
            {
                if (opaqueOnly && wanted[offset + 3] != 255) continue;
                int channels = opaqueOnly ? 3 : 4;
                for (int channel = 0; channel < channels; channel++) if (wanted[offset + channel] != found[offset + channel]) return false;
            }
            return true;
        }

        private static byte[] ReadGlobal(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return null;
            ulong size = GlobalSize(handle).ToUInt64();
            if (size == 0 || size > 64 * 1024 * 1024) throw new InvalidOperationException("Invalid clipboard payload size");
            IntPtr pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero) throw new InvalidOperationException("Could not lock clipboard payload");
            try { byte[] bytes = new byte[(int)size]; Marshal.Copy(pointer, bytes, 0, bytes.Length); return bytes; }
            finally { GlobalUnlock(handle); }
        }

        private static void ReplaceWithText()
        {
            byte[] bytes = Encoding.Unicode.GetBytes(ReplacementText + "\0");
            IntPtr memory = GlobalAlloc(0x0002, (UIntPtr)bytes.Length);
            if (memory == IntPtr.Zero) throw new InvalidOperationException("Replacement allocation failed");
            try
            {
                IntPtr pointer = GlobalLock(memory);
                if (pointer == IntPtr.Zero) throw new InvalidOperationException("Replacement lock failed");
                try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
                finally { GlobalUnlock(memory); }
                if (!EmptyClipboard() || SetClipboardData(UnicodeTextFormat, memory) == IntPtr.Zero) throw new InvalidOperationException("Replacement write failed");
                memory = IntPtr.Zero;
            }
            finally { if (memory != IntPtr.Zero) GlobalFree(memory); }
        }

        private static Process StartHelper(string command, params string[] arguments)
        {
            string args = command;
            foreach (string argument in arguments) args += " \"" + argument + "\"";
            var start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, args) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            Process process = Process.Start(start);
            if (process == null) throw new InvalidOperationException("Could not start clipboard test helper");
            return process;
        }

        private static async Task WaitForExit(Process process, int milliseconds)
        {
            var clock = Stopwatch.StartNew();
            while (!process.HasExited && clock.ElapsedMilliseconds < milliseconds) await Task.Delay(10);
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(1000);
                throw new TimeoutException("Clipboard test helper exceeded " + milliseconds + " ms");
            }
        }

        private static void SignalRelease(string path, string action)
        {
            if (File.Exists(path)) return;
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, action);
            File.Move(temporary, path); // The helper sees a closed, complete signal file.
        }

        private static void Check(bool passed, string name)
        {
            lines.Add((passed ? "PASS " : "FAIL ") + name);
            if (!passed) failures++;
        }

        private sealed class ClipboardLock : IDisposable
        {
            private readonly Process process;
            private readonly string releasePath;
            private ClipboardLock(Process process, string releasePath) { this.process = process; this.releasePath = releasePath; }
            internal bool HasExited { get { return process.HasExited; } }

            internal static async Task<ClipboardLock> Start(string sandbox)
            {
                string prefix = Path.Combine(sandbox, Guid.NewGuid().ToString("N"));
                string ready = prefix + ".ready", release = prefix + ".release";
                var result = new ClipboardLock(StartHelper("--hold-clipboard", ready, release), release);
                try
                {
                    var clock = Stopwatch.StartNew();
                    while (!File.Exists(ready) && !result.process.HasExited && clock.ElapsedMilliseconds < 5000) await Task.Delay(10);
                    if (!File.Exists(ready)) throw new InvalidOperationException("Clipboard lock helper failed to acquire clipboard");
                    return result;
                }
                catch { result.Dispose(); throw; }
            }

            internal async Task Release(string action)
            {
                SignalRelease(releasePath, action);
                await WaitForExit(process, 3000);
                if (process.ExitCode != 0) throw new InvalidOperationException("Clipboard lock helper exited with " + process.ExitCode);
            }

            public void Dispose()
            {
                try
                {
                    if (!process.HasExited)
                    {
                        SignalRelease(releasePath, "release");
                        if (!process.WaitForExit(3000)) { process.Kill(); process.WaitForExit(1000); }
                    }
                }
                finally { process.Dispose(); }
            }
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
        [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string format);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr memory);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
        [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr memory);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder fileName, int count);
    }
}
