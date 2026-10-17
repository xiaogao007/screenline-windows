using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Windows.Media.Imaging;

namespace Screenline
{
    public sealed class ShotItem : INotifyPropertyChanged
    {
        private string _id;
        private string _filePath;
        private DateTime _addedAt;
        private string _displayName;
        private int _pixelWidth;
        private int _pixelHeight;
        private BitmapSource _thumbnail;

        public string Id { get { return _id; } set { _id = value; Changed("Id"); } }
        public string FilePath { get { return _filePath; } set { _filePath = value; Changed("FilePath"); } }
        public DateTime AddedAt { get { return _addedAt; } set { _addedAt = value; Changed("AddedAt"); } }
        public string DisplayName { get { return _displayName; } set { _displayName = value; Changed("DisplayName"); } }
        public int PixelWidth { get { return _pixelWidth; } set { _pixelWidth = value; Changed("PixelWidth"); } }
        public int PixelHeight { get { return _pixelHeight; } set { _pixelHeight = value; Changed("PixelHeight"); } }
        public BitmapSource Thumbnail { get { return _thumbnail; } set { _thumbnail = value; Changed("Thumbnail"); } }
        internal string ContentHash { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Changed(string property)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(property));
        }
    }

    [DataContract]
    public sealed class AppSettings
    {
        [DataMember] public bool WatchClipboard { get; set; }
        [DataMember] public bool WatchFolders { get; set; }
        [DataMember] public bool RevealOnCapture { get; set; }
        [DataMember] public bool HideInFullscreen { get; set; }
        [DataMember] public bool CheckUpdatesOnStartup { get; set; }
        [DataMember] public string[] WatchPaths { get; set; }

        public AppSettings() { SetDefaults(); }
        [OnDeserializing]
        private void BeforeDeserialize(StreamingContext context) { SetDefaults(); }

        private void SetDefaults()
        {
            WatchClipboard = false;
            WatchFolders = true;
            RevealOnCapture = true;
            HideInFullscreen = true;
            CheckUpdatesOnStartup = false;
            List<string> paths = new List<string>();
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (!String.IsNullOrEmpty(pictures)) paths.Add(Path.Combine(pictures, "Screenshots"));
            string[] cloudRoots = new string[] { Environment.GetEnvironmentVariable("OneDrive"),
                Environment.GetEnvironmentVariable("OneDriveConsumer"), Environment.GetEnvironmentVariable("OneDriveCommercial") };
            foreach (string root in cloudRoots)
            {
                if (String.IsNullOrEmpty(root)) continue;
                string path = Path.Combine(root, "Pictures", "Screenshots");
                bool found = paths.Exists(delegate(string p) { return String.Equals(p, path, StringComparison.OrdinalIgnoreCase); });
                if (!found) paths.Add(path);
            }
            WatchPaths = paths.ToArray();
        }
    }

    public static class SettingsStore
    {
        public static AppSettings Load(string root)
        {
            string file = Path.Combine(root, "settings.json");
            string[] candidates = new string[] { file, file + ".bak", file + ".tmp" };
            foreach (string candidate in candidates)
            {
                try
                {
                    if (!File.Exists(candidate)) continue;
                    AppSettings value = StorageFiles.ReadJson<AppSettings>(candidate);
                    if (value == null) continue;
                    if (value.WatchPaths == null) value.WatchPaths = new AppSettings().WatchPaths;
                    // A backup may predate a user's decision to disable clipboard
                    // monitoring or startup network access. Require fresh consent.
                    if (!String.Equals(candidate, file, StringComparison.OrdinalIgnoreCase))
                    {
                        value.WatchClipboard = false;
                        value.CheckUpdatesOnStartup = false;
                    }
                    return value;
                }
                catch (Exception ex)
                {
                    if (!StorageFiles.IsFileError(ex)) throw;
                }
            }
            return new AppSettings();
        }

        public static void Save(string root, AppSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            Directory.CreateDirectory(root);
            StorageFiles.WriteJsonAtomic(Path.Combine(root, "settings.json"), settings);
        }
    }

    internal static class StorageFiles
    {
        internal static T ReadJson<T>(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > 16L * 1024L * 1024L) throw new InvalidDataException("The index is too large.");
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
                return (T)serializer.ReadObject(stream);
            }
        }

        internal static void WriteJsonAtomic<T>(string path, T value)
        {
            string temporary = path + ".tmp";
            using (FileStream stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
                serializer.WriteObject(stream, value);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", true);
            else File.Move(temporary, path);
        }

        internal static bool IsFileError(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is SerializationException ||
                ex is System.Xml.XmlException || ex is ArgumentException || ex is NotSupportedException ||
                ex is System.Security.SecurityException || ex is InvalidOperationException || ex is System.Runtime.InteropServices.COMException;
        }
    }
}
