using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;

namespace Screenline
{
    public sealed class LibraryStore
    {
        public const long MaximumFileBytes = 100L * 1024L * 1024L;
        private const long MaximumPixels = 100000000L;
        private readonly string _imagesPath;
        private readonly string _manifestPath;
        private readonly Dictionary<string, ShotItem> _hashes = new Dictionary<string, ShotItem>(StringComparer.OrdinalIgnoreCase);
        private List<string> _lastRemoved = new List<string>();

        public LibraryStore(string root)
        {
            if (String.IsNullOrWhiteSpace(root)) throw new ArgumentException("A library directory is required.", "root");
            RootPath = Path.GetFullPath(root);
            _imagesPath = Path.Combine(RootPath, "images");
            _manifestPath = Path.Combine(RootPath, "library.json");
            Items = new ObservableCollection<ShotItem>();
            TrashItems = new ObservableCollection<ShotItem>();
            Directory.CreateDirectory(_imagesPath);
        }

        public string RootPath { get; private set; }
        public ObservableCollection<ShotItem> Items { get; private set; }
        // Removed images stay in the managed images directory and this persistent trash index.
        // They are never deleted automatically, and can be restored by reimport or Undo.
        public ObservableCollection<ShotItem> TrashItems { get; private set; }
        public event Action<string> Status;

        public void Load()
        {
            string[] candidates = new string[] { _manifestPath, _manifestPath + ".bak", _manifestPath + ".tmp" };
            LibraryManifest manifest = null;
            string loadedFrom = null;
            bool hadIndex = false;
            foreach (string file in candidates)
            {
                try
                {
                    if (!File.Exists(file)) continue;
                    hadIndex = true;
                    LibraryManifest candidate = StorageFiles.ReadJson<LibraryManifest>(file);
                    if (candidate == null || candidate.Version != 1 || candidate.Items == null)
                        throw new InvalidDataException("Unsupported library index.");
                    manifest = candidate;
                    loadedFrom = file;
                    break;
                }
                catch (Exception ex)
                {
                    if (!StorageFiles.IsFileError(ex)) throw;
                }
            }
            if (manifest == null)
            {
                if (hadIndex) Tell("图片索引暂时无法读取。图片文件仍保存在：" + _imagesPath);
                return;
            }

            List<ShotItem> active = new List<ShotItem>();
            List<ShotItem> trash = new List<ShotItem>();
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int unavailable = 0;
            int invalid = 0;
            foreach (ShotRecord record in manifest.Items)
            {
                ShotItem item = ReadRecord(record, ids, ref unavailable);
                if (item != null) active.Add(item); else invalid++;
            }
            if (manifest.Trash != null)
            {
                foreach (ShotRecord record in manifest.Trash)
                {
                    ShotItem item = ReadRecord(record, ids, ref unavailable);
                    if (item != null) trash.Add(item); else invalid++;
                }
            }
            ReplaceCollection(Items, active.OrderByDescending(delegate(ShotItem item) { return item.AddedAt; }));
            ReplaceCollection(TrashItems, trash.OrderByDescending(delegate(ShotItem item) { return item.AddedAt; }));
            _lastRemoved = manifest.LastRemoved ?? new List<string>();
            RebuildHashes();
            if (!String.Equals(loadedFrom, _manifestPath, StringComparison.OrdinalIgnoreCase))
                Tell("已从备份恢复图片索引，共 " + Items.Count + " 张。");
            else if (unavailable != 0 || invalid != 0)
                Tell("索引已载入；" + unavailable + " 张图片暂不可读，" + invalid + " 条无效记录已跳过。原文件均已保留。");
        }

        public bool Save()
        {
            try { SaveCore(); return true; }
            catch (Exception ex)
            {
                if (!StorageFiles.IsFileError(ex)) throw;
                Tell("无法保存图片索引：" + ex.Message);
                return false;
            }
        }

        public static bool IsSupportedFile(string path)
        {
            if (String.IsNullOrEmpty(path)) return false;
            string extension;
            try { extension = Path.GetExtension(path).ToLowerInvariant(); }
            catch (ArgumentException) { return false; }
            return extension == ".png" || extension == ".jpg" || extension == ".jpeg" || extension == ".bmp" || extension == ".gif";
        }

        public ShotItem ImportFile(string sourcePath)
        {
            return ImportFile(sourcePath, false);
        }

        // Watchers suppress transient failures while a screenshot is still being written.
        internal ShotItem ImportFile(string sourcePath, bool quietFailure)
        {
            if (!IsSupportedFile(sourcePath))
            {
                if (!quietFailure) Tell("请选择 PNG、JPG、BMP 或 GIF 图片。");
                return null;
            }
            string temporary = Path.Combine(_imagesPath, Guid.NewGuid().ToString("N") + ".import");
            try
            {
                string absolute = Path.GetFullPath(sourcePath);
                using (FileStream input = new FileStream(absolute, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (input.Length == 0 || input.Length > MaximumFileBytes)
                        throw new InvalidDataException("图片为空或超过 100 MB。");
                    using (FileStream output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        byte[] buffer = new byte[81920];
                        long total = 0;
                        int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            total += count;
                            if (total > MaximumFileBytes) throw new InvalidDataException("图片超过 100 MB。");
                            output.Write(buffer, 0, count);
                        }
                        output.Flush(true);
                    }
                }
                return ImportTemporary(temporary, Path.GetExtension(absolute).ToLowerInvariant(), Path.GetFileNameWithoutExtension(absolute));
            }
            catch (Exception ex)
            {
                if (!IsImageError(ex)) throw;
                if (!quietFailure) Tell("无法导入图片：" + ex.Message);
                return null;
            }
            finally { DeleteTemporary(temporary); }
        }

        public ShotItem ImportBitmap(BitmapSource bitmap)
        {
            if (bitmap == null) return null;
            string temporary = Path.Combine(_imagesPath, Guid.NewGuid().ToString("N") + ".import");
            try
            {
                ValidateDimensions(bitmap.PixelWidth, bitmap.PixelHeight);
                PngBitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    encoder.Save(stream);
                    if (stream.Length > MaximumFileBytes) throw new InvalidDataException("图片超过 100 MB。");
                    stream.Flush(true);
                }
                return ImportTemporary(temporary, ".png", "截图 " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss"));
            }
            catch (Exception ex)
            {
                if (!IsImageError(ex)) throw;
                Tell("无法导入剪贴板图片：" + ex.Message);
                return null;
            }
            finally { DeleteTemporary(temporary); }
        }

        public bool Remove(ShotItem item)
        {
            if (item == null || !Items.Contains(item)) return false;
            bool saved = CommitMutation(delegate
            {
                Items.Remove(item);
                TrashItems.Insert(0, item);
                _lastRemoved = new List<string>(new string[] { item.Id });
            });
            if (saved) Tell("已从晾台移除，原图保留，可撤销。");
            return saved;
        }

        public int RemoveAll()
        {
            int count = Items.Count;
            if (count == 0) return 0;
            bool saved = CommitMutation(delegate
            {
                _lastRemoved = Items.Select(delegate(ShotItem item) { return item.Id; }).ToList();
                foreach (ShotItem item in Items.ToArray()) TrashItems.Insert(0, item);
                Items.Clear();
            });
            if (saved) Tell("已清空晾台，" + count + " 张原图保留，可撤销。");
            return saved ? count : 0;
        }

        public bool UndoLastRemove()
        {
            HashSet<string> restoreIds = new HashSet<string>(_lastRemoved, StringComparer.OrdinalIgnoreCase);
            ShotItem[] restore = TrashItems.Where(delegate(ShotItem item) { return restoreIds.Contains(item.Id); }).ToArray();
            if (restore.Length == 0) { Tell("没有可撤销的移除操作。"); return false; }
            bool saved = CommitMutation(delegate
            {
                foreach (ShotItem item in restore)
                {
                    TrashItems.Remove(item);
                    InsertNewest(item);
                }
                _lastRemoved.Clear();
            });
            if (saved) Tell("已恢复 " + restore.Length + " 张图片。");
            return saved;
        }

        private ShotItem ImportTemporary(string temporary, string extension, string displayName)
        {
            int width;
            int height;
            BitmapSource thumbnail = ReadThumbnail(temporary, out width, out height);
            string hash = ComputeHash(temporary);
            ShotItem duplicate;
            if (_hashes.TryGetValue(hash, out duplicate))
            {
                Tell("这张图片已在晾台中。");
                return duplicate;
            }
            ShotItem removed = TrashItems.FirstOrDefault(delegate(ShotItem item) { return String.Equals(item.ContentHash, hash, StringComparison.OrdinalIgnoreCase); });
            if (removed != null)
            {
                // Restore a retained item without duplicating its managed file.
                if (!File.Exists(removed.FilePath)) File.Copy(temporary, removed.FilePath, false);
                bool restored = CommitMutation(delegate
                {
                    TrashItems.Remove(removed);
                    InsertNewest(removed);
                    _lastRemoved.Remove(removed.Id);
                });
                if (restored) { Tell("已恢复：" + removed.DisplayName); return removed; }
                return null;
            }

            string id = Guid.NewGuid().ToString("N");
            string destination = Path.Combine(_imagesPath, id + extension);
            File.Move(temporary, destination);
            ShotItem added = new ShotItem
            {
                Id = id, FilePath = destination, AddedAt = DateTime.Now,
                DisplayName = String.IsNullOrWhiteSpace(displayName) ? "截图" : displayName,
                PixelWidth = width, PixelHeight = height, Thumbnail = thumbnail, ContentHash = hash
            };
            if (!CommitMutation(delegate { Items.Insert(0, added); }))
            {
                Tell("索引未能保存；图片副本仍安全保存在：" + destination);
                return null;
            }
            Tell("已收下：" + added.DisplayName);
            return added;
        }

        private bool CommitMutation(Action action)
        {
            ShotItem[] active = Items.ToArray();
            ShotItem[] trash = TrashItems.ToArray();
            List<string> lastRemoved = new List<string>(_lastRemoved);
            try
            {
                action();
                RebuildHashes();
                SaveCore();
                return true;
            }
            catch (Exception ex)
            {
                ReplaceCollection(Items, active);
                ReplaceCollection(TrashItems, trash);
                _lastRemoved = lastRemoved;
                RebuildHashes();
                if (!StorageFiles.IsFileError(ex)) throw;
                Tell("更改未保存，已恢复原来的列表：" + ex.Message);
                return false;
            }
        }

        private void SaveCore()
        {
            Directory.CreateDirectory(RootPath);
            LibraryManifest manifest = new LibraryManifest
            {
                Version = 1,
                Items = Items.Select(ShotRecord.FromItem).ToList(),
                Trash = TrashItems.Select(ShotRecord.FromItem).ToList(),
                LastRemoved = new List<string>(_lastRemoved)
            };
            StorageFiles.WriteJsonAtomic(_manifestPath, manifest);
        }

        private ShotItem ReadRecord(ShotRecord record, HashSet<string> ids, ref int unavailable)
        {
            if (record == null || String.IsNullOrEmpty(record.Id) || String.IsNullOrEmpty(record.FileName)) return null;
            Guid parsed;
            if (!Guid.TryParseExact(record.Id, "N", out parsed)) return null;
            if (record.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            if (Path.GetFileName(record.FileName) != record.FileName || !IsSupportedFile(record.FileName)) return null;
            if (!String.Equals(Path.GetFileNameWithoutExtension(record.FileName), record.Id, StringComparison.OrdinalIgnoreCase)) return null;
            if (!ids.Add(record.Id)) return null;
            string path = Path.Combine(_imagesPath, record.FileName);
            ShotItem item = new ShotItem
            {
                Id = record.Id, FilePath = path, AddedAt = record.AddedAt,
                DisplayName = String.IsNullOrWhiteSpace(record.DisplayName) ? "截图" : record.DisplayName,
                PixelWidth = record.PixelWidth, PixelHeight = record.PixelHeight, ContentHash = record.Hash
            };
            try
            {
                int width;
                int height;
                item.Thumbnail = ReadThumbnail(path, out width, out height);
                item.PixelWidth = width;
                item.PixelHeight = height;
                if (String.IsNullOrEmpty(item.ContentHash)) item.ContentHash = ComputeHash(path);
            }
            catch (Exception ex)
            {
                if (!IsImageError(ex)) throw;
                unavailable++;
            }
            return item;
        }

        internal static BitmapSource ReadThumbnail(string path, out int width, out int height)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length == 0 || stream.Length > MaximumFileBytes) throw new InvalidDataException("图片为空或超过 100 MB。");
                BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnDemand);
                if (!(decoder is PngBitmapDecoder) && !(decoder is JpegBitmapDecoder) &&
                    !(decoder is BmpBitmapDecoder) && !(decoder is GifBitmapDecoder))
                    throw new InvalidDataException("仅支持 PNG、JPG、BMP 或 GIF 图片内容。");
                if (decoder.Frames.Count == 0) throw new InvalidDataException("没有可读取的图像帧。");
                width = decoder.Frames[0].PixelWidth;
                height = decoder.Frames[0].PixelHeight;
                ValidateDimensions(width, height);
            }
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                BitmapImage thumbnail = new BitmapImage();
                thumbnail.BeginInit();
                thumbnail.CacheOption = BitmapCacheOption.OnLoad;
                thumbnail.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (width >= height && width > 440) thumbnail.DecodePixelWidth = 440;
                else if (height > 440) thumbnail.DecodePixelHeight = 440;
                thumbnail.StreamSource = stream;
                thumbnail.EndInit();
                thumbnail.Freeze();
                return thumbnail;
            }
        }

        private static void ValidateDimensions(int width, int height)
        {
            if (width < 1 || height < 1 || (long)width * height > MaximumPixels)
                throw new InvalidDataException("图片尺寸无效或超过 1 亿像素。");
        }

        private static string ComputeHash(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private void RebuildHashes()
        {
            _hashes.Clear();
            foreach (ShotItem item in Items)
                if (!String.IsNullOrEmpty(item.ContentHash) && File.Exists(item.FilePath) && !_hashes.ContainsKey(item.ContentHash))
                    _hashes.Add(item.ContentHash, item);
        }

        private void InsertNewest(ShotItem item)
        {
            int index = 0;
            while (index < Items.Count && Items[index].AddedAt >= item.AddedAt) index++;
            Items.Insert(index, item);
        }

        private static void ReplaceCollection(ObservableCollection<ShotItem> target, IEnumerable<ShotItem> source)
        {
            target.Clear();
            foreach (ShotItem item in source) target.Add(item);
        }

        private static bool IsImageError(Exception ex)
        {
            return StorageFiles.IsFileError(ex) || ex is System.IO.FileFormatException || ex is OverflowException;
        }

        private static void DeleteTemporary(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void Tell(string value)
        {
            Action<string> handler = Status;
            if (handler != null) handler(value);
        }

        [DataContract]
        private sealed class LibraryManifest
        {
            [DataMember] public int Version { get; set; }
            [DataMember] public List<ShotRecord> Items { get; set; }
            [DataMember] public List<ShotRecord> Trash { get; set; }
            [DataMember] public List<string> LastRemoved { get; set; }
        }

        [DataContract]
        private sealed class ShotRecord
        {
            [DataMember] public string Id { get; set; }
            [DataMember] public string FileName { get; set; }
            [DataMember] public DateTime AddedAt { get; set; }
            [DataMember] public string DisplayName { get; set; }
            [DataMember] public int PixelWidth { get; set; }
            [DataMember] public int PixelHeight { get; set; }
            [DataMember] public string Hash { get; set; }

            internal static ShotRecord FromItem(ShotItem item)
            {
                return new ShotRecord
                {
                    Id = item.Id, FileName = Path.GetFileName(item.FilePath), AddedAt = item.AddedAt,
                    DisplayName = item.DisplayName, PixelWidth = item.PixelWidth, PixelHeight = item.PixelHeight,
                    Hash = item.ContentHash
                };
            }
        }
    }
}
