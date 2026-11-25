using System;
using System.IO;
using System.IO.Compression;
using System.Drawing;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace RcloneOssManager
{
    /// <summary>仅保存工具和配置文件路径；连接认证仍由rclone配置独立管理，不写入EXE。</summary>
    internal sealed class RuntimePaths
    {
        public string RcloneExecutable { get; set; }
        public string RcloneConfiguration { get; set; }
        /// <summary>用户指定的文件管理器程序；留空时自动探测 Pulse 或 Directory Opus。</summary>
        public string FileManagerExecutable { get; set; }
        /// <summary>需要 rclone 挂载时追加 --use-server-modtime 的连接标识；默认空。</summary>
        public string[] ServerModtimeRemotes { get; set; }
        /// <summary>界面主题：light 或 dark；空值按浅色处理。</summary>
        public string Theme { get; set; }
        /// <summary>挂载缓存目录；空值使用 rclone 默认位置。</summary>
        public string CacheDirectory { get; set; }
        /// <summary>目录列表缓存时长，例如 12h；空值使用 rclone 默认。</summary>
        public string DirCacheTime { get; set; }
        /// <summary>VFS 缓存上限，例如 20G；空值使用 rclone 默认（无上限）。</summary>
        public string VfsCacheMaxSize { get; set; }
        /// <summary>VFS 缓存最长保留时间，例如 24h；空值使用 rclone 默认（无限制）。</summary>
        public string VfsCacheMaxAge { get; set; }
        /// <summary>传输缓冲大小，例如 64M；空值使用 rclone 默认。</summary>
        public string BufferSize { get; set; }
        private string settingsFile, baseFolder, dataFolder;
        private static RuntimePaths current;
        internal static RuntimePaths Current { get { return current ?? (current = Load()); } }
        internal string SettingsFile { get { return settingsFile; } }

        /// <summary>适合写回设置的 rclone 路径；内嵌引擎释放路径返回空，避免下次启动依赖临时目录。</summary>
        internal string PersistableExecutable
        {
            get
            {
                if (String.IsNullOrWhiteSpace(RcloneExecutable)) return String.Empty;
                try
                {
                    var full = Path.GetFullPath(RcloneExecutable);
                    var runtimeRoot = Path.GetFullPath(Path.Combine(dataFolder, "runtime")) + Path.DirectorySeparatorChar;
                    return full.StartsWith(runtimeRoot, StringComparison.OrdinalIgnoreCase) ? String.Empty : RcloneExecutable;
                }
                catch (Exception) { return RcloneExecutable; }
            }
        }

        /// <summary>加载路径设置；优先使用程序目录旁的设置，其次使用用户数据目录。</summary>
        /// <param name="baseDirectory">程序目录，测试时可独立指定。</param><param name="dataDirectory">用户数据目录。</param><returns>路径设置。</returns>
        internal static RuntimePaths Load(string baseDirectory = null, string dataDirectory = null)
        {
            var folder = baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory;
            var data = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RcloneOssManager");
            var sidecar = Path.Combine(folder, "RcloneOssManager.settings.json");
            var settings = File.Exists(sidecar) ? sidecar : Path.Combine(data, "settings.json");
            var value = File.Exists(settings) ? new JavaScriptSerializer().Deserialize<RuntimePaths>(File.ReadAllText(settings, Encoding.UTF8)) : new RuntimePaths();
            if (value == null) throw new InvalidOperationException("路径设置文件格式无效。");
            value.settingsFile = settings; value.baseFolder = folder; value.dataFolder = data;
            if (String.IsNullOrWhiteSpace(value.RcloneExecutable))
            {
                var local = Path.Combine(folder, "rclone.exe");
                value.RcloneExecutable = File.Exists(local) ? local : "";
            }
            if (String.IsNullOrWhiteSpace(value.RcloneConfiguration)) value.RcloneConfiguration = Path.Combine(data, "rclone.conf");
            if (value.RcloneExecutable != "") value.RcloneExecutable = value.FullPath(value.RcloneExecutable);
            value.RcloneConfiguration = value.FullPath(value.RcloneConfiguration);
            return value;
        }

        /// <summary>将相对路径固定到程序所在目录，而不是进程当前工作目录。</summary>
        /// <param name="path">设置中的路径。</param><returns>完整路径。</returns>
        private string FullPath(string path) { return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseFolder, path)); }

        /// <summary>优先使用指定rclone，否则释放内嵌引擎并校验SHA-256；不读取连接认证。</summary>
        /// <returns>可执行文件完整路径。</returns>
        internal string Executable()
        {
            if (!String.IsNullOrWhiteSpace(RcloneExecutable)) return RcloneExecutable;
            lock (typeof(RuntimePaths))
            {
                var assembly = Assembly.GetExecutingAssembly(); string hash;
                using (var stream = assembly.GetManifestResourceStream("rclone.sha256"))
                {
                    if (stream == null) throw new InvalidOperationException("此构建没有内嵌rclone。请在设置中选择rclone.exe，或使用单文件发行版。");
                    using (var reader = new StreamReader(stream)) hash = reader.ReadToEnd().Trim();
                }
                if (!System.Text.RegularExpressions.Regex.IsMatch(hash, "^[a-f0-9]{64}$")) throw new InvalidOperationException("内嵌引擎校验记录无效。");
                var folder = Path.Combine(dataFolder, "runtime", hash.Substring(0, 16)); Directory.CreateDirectory(folder);
                var target = Path.Combine(folder, "rclone.exe");
                if (!File.Exists(target) || Hash(target) != hash)
                {
                    var stage = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        using (var source = assembly.GetManifestResourceStream("rclone.exe.gz"))
                        {
                            if (source == null) throw new InvalidOperationException("内嵌rclone资源缺失。");
                            using (var zip = new GZipStream(source, CompressionMode.Decompress)) using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None)) zip.CopyTo(output);
                        }
                        if (Hash(stage) != hash) throw new InvalidOperationException("内嵌rclone校验失败，未启动。");
                        if (File.Exists(target)) File.Replace(stage, target, null); else File.Move(stage, target);
                    }
                    finally { if (File.Exists(stage)) File.Delete(stage); }
                }
                RcloneExecutable = target; return target;
            }
        }

        /// <summary>新电脑仅创建默认空配置，不覆盖已有配置或静默创建用户选错的自定义路径。</summary>
        internal void EnsureConfiguration()
        {
            if (File.Exists(RcloneConfiguration)) return;
            if (RcloneConfiguration != Path.Combine(dataFolder, "rclone.conf")) throw new InvalidOperationException("未找到指定rclone配置，请打开路径设置重新选择。");
            Directory.CreateDirectory(dataFolder);
            using (new FileStream(RcloneConfiguration, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
        }

        /// <summary>原子保存路径选项，正在运行的客户端保持当前设置，重启后统一生效。</summary>
        /// <param name="executable">明确选择的rclone程序。</param><param name="configuration">配置文件路径。</param><param name="fileManagerExecutable">文件管理器程序，可空。</param><param name="theme">界面主题 light 或 dark，可空表示保持不变。</param>
        internal void Save(string executable, string configuration, string fileManagerExecutable = null, string theme = null)
        {
            executable = String.IsNullOrWhiteSpace(executable) ? String.Empty : FullPath(executable); configuration = FullPath(configuration);
            var fileManager = String.IsNullOrWhiteSpace(fileManagerExecutable) ? null : FullPath(fileManagerExecutable.Trim());
            var selectedTheme = String.IsNullOrWhiteSpace(theme) ? Theme : theme.Trim().ToLowerInvariant();
            if (executable.Length > 0 && (!File.Exists(executable) || !String.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("请选择存在的rclone.exe。");
            if (!File.Exists(configuration)) throw new ArgumentException("请选择存在的rclone配置文件。");
            if (fileManager != null && (!File.Exists(fileManager) || !String.Equals(Path.GetExtension(fileManager), ".exe", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("请选择存在的文件管理器程序。");
            Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)); var stage = settingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(stage, new JavaScriptSerializer().Serialize(new RuntimePaths { RcloneExecutable = executable, RcloneConfiguration = configuration, FileManagerExecutable = fileManager, ServerModtimeRemotes = ServerModtimeRemotes, Theme = selectedTheme, CacheDirectory = CacheDirectory, DirCacheTime = DirCacheTime, VfsCacheMaxSize = VfsCacheMaxSize, VfsCacheMaxAge = VfsCacheMaxAge, BufferSize = BufferSize }), new UTF8Encoding(false));
                if (File.Exists(settingsFile)) File.Replace(stage, settingsFile, settingsFile + ".bak"); else File.Move(stage, settingsFile);
                var saved = new JavaScriptSerializer().Deserialize<RuntimePaths>(File.ReadAllText(settingsFile, Encoding.UTF8));
                if (saved.RcloneExecutable != executable || saved.RcloneConfiguration != configuration || saved.FileManagerExecutable != fileManager || saved.Theme != selectedTheme) throw new IOException("路径设置读回核验失败。");
            }
            finally { if (File.Exists(stage)) File.Delete(stage); }
        }

        /// <summary>按流计算文件摘要，避免把rclone整体读入内存。</summary>
        /// <param name="path">文件路径。</param><returns>小写SHA-256。</returns>
        private static string Hash(string path) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }

        /// <summary>从程序集内读取图标，单文件发行版不依赖外部ico。</summary>
        /// <returns>调用者拥有的图标实例。</returns>
        internal static Icon LoadIcon()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("oss-manager.ico"))
                if (stream != null) using (var icon = new Icon(stream)) return (Icon)icon.Clone();
            var local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "oss-manager.ico"); return File.Exists(local) ? new Icon(local) : (Icon)SystemIcons.Application.Clone();
        }
    }
}
