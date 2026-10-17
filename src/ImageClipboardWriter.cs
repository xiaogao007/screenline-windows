using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Screenline
{
    internal sealed class ClipboardBusyException : Exception
    {
        internal ClipboardBusyException() : base("剪贴板正忙，请稍后再次点击图片复制。") { }
    }

    internal static class ImageClipboardWriter
    {
        private const uint DibFormat = 8;
        private const uint FileDropFormat = 15;
        private const int OpenAttempts = 20;
        private const int RetryDelayMilliseconds = 100;

        internal static async Task CopyAsync(BitmapSource image, string filePath, IntPtr owner, CancellationToken cancellationToken)
        {
            if (image == null) throw new ArgumentNullException("image");
            if (owner == IntPtr.Zero) throw new ArgumentException("A clipboard owner window is required.", "owner");
            cancellationToken.ThrowIfCancellationRequested();
            uint originalSequence = GetClipboardSequenceNumber();

            // Finish encoding and allocate every handle before changing the clipboard.
            byte[] pngBytes = CreatePngPayload(image);
            cancellationToken.ThrowIfCancellationRequested();
            byte[] dibBytes = CreateDibPayload(image);
            cancellationToken.ThrowIfCancellationRequested();
            byte[] fileDropBytes = CreateFileDropPayload(filePath);
            uint markerFormat = RegisterFormat(CaptureSources.SelfClipboardFormat);
            uint pngFormat = RegisterFormat("PNG");
            using (ClipboardPayload marker = new ClipboardPayload(markerFormat, new byte[] { 1 }))
            using (ClipboardPayload dib = new ClipboardPayload(DibFormat, dibBytes))
            using (ClipboardPayload png = new ClipboardPayload(pngFormat, pngBytes))
            using (ClipboardPayload fileDrop = new ClipboardPayload(FileDropFormat, fileDropBytes))
            {
                for (int attempt = 0; attempt < OpenAttempts; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureUnchanged(originalSequence);
                    if (OpenClipboard(owner))
                    {
                        try
                        {
                            // The open lock makes this final sequence check stable.
                            cancellationToken.ThrowIfCancellationRequested();
                            EnsureUnchanged(originalSequence);
                            if (!EmptyClipboard()) throw NativeFailure("无法准备图片剪贴板，请再次点击复制。");
                            // Publish the exclusion marker before any image format so our
                            // monitor cannot re-import this image after a partial failure.
                            marker.Transfer();
                            dib.Transfer();
                            png.Transfer();
                            fileDrop.Transfer();
                            return;
                        }
                        finally { CloseClipboard(); }
                    }
                    if (attempt + 1 < OpenAttempts)
                        await Task.Delay(RetryDelayMilliseconds, cancellationToken);
                }
                EnsureUnchanged(originalSequence);
                throw new ClipboardBusyException();
            }
        }

        internal static byte[] CreatePngPayload(BitmapSource image)
        {
            if (image == null) throw new ArgumentNullException("image");
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (MemoryStream stream = new MemoryStream())
            {
                encoder.Save(stream);
                return stream.ToArray();
            }
        }

        internal static byte[] CreateDibPayload(BitmapSource image)
        {
            if (image == null) throw new ArgumentNullException("image");
            BitmapSource bgr = image.Format == PixelFormats.Bgr24 ? image :
                new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
            BmpBitmapEncoder encoder = new BmpBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bgr));
            byte[] bitmap;
            using (MemoryStream stream = new MemoryStream())
            {
                encoder.Save(stream);
                bitmap = stream.ToArray();
            }

            // CF_DIB contains the BMP information header and bottom-up, DWORD-padded
            // BGR rows, but excludes the 14-byte BITMAPFILEHEADER.
            if (bitmap.Length < 54 || bitmap[0] != (byte)'B' || bitmap[1] != (byte)'M')
                throw new InvalidDataException("无法生成图片剪贴板数据。");
            uint informationBytes = BitConverter.ToUInt32(bitmap, 14);
            uint pixelOffset = BitConverter.ToUInt32(bitmap, 10);
            long stride = ((long)image.PixelWidth * 3 + 3) & ~3L;
            long pixelBytes = stride * image.PixelHeight;
            if (informationBytes < 40 || pixelOffset != 14L + informationBytes ||
                image.PixelWidth <= 0 || image.PixelHeight <= 0 ||
                BitConverter.ToInt32(bitmap, 18) != image.PixelWidth ||
                BitConverter.ToInt32(bitmap, 22) != image.PixelHeight ||
                BitConverter.ToUInt16(bitmap, 26) != 1 ||
                BitConverter.ToUInt16(bitmap, 28) != 24 ||
                BitConverter.ToUInt32(bitmap, 30) != 0 ||
                pixelBytes > bitmap.LongLength - pixelOffset)
                throw new InvalidDataException("图片剪贴板数据格式无效。");
            byte[] dib = new byte[bitmap.Length - 14];
            Buffer.BlockCopy(bitmap, 14, dib, 0, dib.Length);
            return dib;
        }

        internal static byte[] CreateFileDropPayload(string filePath)
        {
            if (String.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("An image file path is required.", "filePath");
            string absolutePath = Path.GetFullPath(filePath);
            byte[] names = Encoding.Unicode.GetBytes(absolutePath + "\0\0");
            byte[] payload = new byte[checked(20 + names.Length)];
            // DROPFILES: DWORD pFiles, POINT pt, BOOL fNC, BOOL fWide.
            Buffer.BlockCopy(BitConverter.GetBytes(20), 0, payload, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(1), 0, payload, 16, 4);
            Buffer.BlockCopy(names, 0, payload, 20, names.Length);
            return payload;
        }

        private static void EnsureUnchanged(uint originalSequence)
        {
            if (GetClipboardSequenceNumber() != originalSequence)
                throw new InvalidOperationException("剪贴板已更新，请再次点击图片复制。");
        }

        private static uint RegisterFormat(string name)
        {
            uint format = RegisterClipboardFormat(name);
            if (format == 0) throw NativeFailure("无法准备图片剪贴板格式。");
            return format;
        }

        private static Win32Exception NativeFailure(string message)
        {
            return new Win32Exception(Marshal.GetLastWin32Error(), message);
        }

        private sealed class ClipboardPayload : IDisposable
        {
            private readonly uint _format;
            private IntPtr _handle;

            internal ClipboardPayload(uint format, byte[] bytes)
            {
                _format = format;
                _handle = GlobalAlloc(0x0002, new UIntPtr((uint)bytes.Length)); // GMEM_MOVEABLE
                if (_handle == IntPtr.Zero) throw new OutOfMemoryException("无法分配图片剪贴板内存。");
                try
                {
                    IntPtr memory = GlobalLock(_handle);
                    if (memory == IntPtr.Zero) throw NativeFailure("无法准备图片剪贴板内存。");
                    try { Marshal.Copy(bytes, 0, memory, bytes.Length); }
                    finally { GlobalUnlock(_handle); }
                }
                catch { Dispose(); throw; }
            }

            internal void Transfer()
            {
                if (SetClipboardData(_format, _handle) == IntPtr.Zero)
                    throw NativeFailure("无法写入图片剪贴板，请再次点击复制。");
                // SetClipboardData transfers this movable handle to Windows.
                _handle = IntPtr.Zero;
            }

            public void Dispose()
            {
                if (_handle == IntPtr.Zero) return;
                GlobalFree(_handle);
                _handle = IntPtr.Zero;
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EmptyClipboard();
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint RegisterClipboardFormat(string name);
        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalUnlock(IntPtr memory);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr memory);
    }
}
