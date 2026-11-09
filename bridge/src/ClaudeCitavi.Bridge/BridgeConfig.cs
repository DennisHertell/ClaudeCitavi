using System;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json;

namespace ClaudeCitavi.Bridge
{
    /// <summary>
    /// Shared configuration between the Citavi add-on and the MCP server.
    /// Stored in %APPDATA%\ClaudeCitavi\bridge.json and created on first start.
    /// The token prevents other local programs from talking to Citavi unnoticed.
    /// </summary>
    public sealed class BridgeConfig
    {
        public const int DefaultPort = 8765;

        [JsonProperty("port")] public int Port { get; set; } = DefaultPort;
        [JsonProperty("token")] public string Token { get; set; }
        [JsonProperty("readOnly")] public bool ReadOnly { get; set; }

        /// <summary>Default target folder for attachments.download. Empty = Documents\ClaudeCitavi\Downloads.</summary>
        [JsonProperty("downloadFolder", NullValueHandling = NullValueHandling.Ignore)] public string DownloadFolder { get; set; }

        /// <summary>Extra folders with custom citation styles (*.ccs), e.g. a relocated "Citavi 6\Custom Citation Styles".</summary>
        [JsonProperty("citationStyleFolders", NullValueHandling = NullValueHandling.Ignore)] public string[] CitationStyleFolders { get; set; }

        /// <summary>Projects (names as in Citavi's start window) opened automatically when Citavi starts.</summary>
        [JsonProperty("autoOpenProjects", NullValueHandling = NullValueHandling.Ignore)] public string[] AutoOpenProjects { get; set; }

        /// <summary>Configuration loaded at add-on start.</summary>
        [JsonIgnore] public static BridgeConfig Current { get; private set; }

        [JsonIgnore]
        public string EffectiveDownloadFolder => string.IsNullOrWhiteSpace(DownloadFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ClaudeCitavi", "Downloads")
            : Environment.ExpandEnvironmentVariables(DownloadFolder);

        public static string Directory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeCitavi");

        public static string FilePath => Path.Combine(Directory, "bridge.json");

        public static BridgeConfig LoadOrCreate()
        {
            System.IO.Directory.CreateDirectory(Directory);

            BridgeConfig config = null;
            if (File.Exists(FilePath))
            {
                config = JsonConvert.DeserializeObject<BridgeConfig>(File.ReadAllText(FilePath));
            }

            config = config ?? new BridgeConfig();
            if (config.Port <= 0) config.Port = DefaultPort;
            if (string.IsNullOrWhiteSpace(config.Token))
            {
                config.Token = NewToken();
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(config, Formatting.Indented));
            }
            Current = config;
            return config;
        }

        private static string NewToken()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }

    internal static class BridgeLog
    {
        public static string LogFilePath => Path.Combine(BridgeConfig.Directory, "bridge.log");

        public static void Write(string message)
        {
            try
            {
                System.IO.Directory.CreateDirectory(BridgeConfig.Directory);
                File.AppendAllText(LogFilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never break Citavi.
            }
        }
    }
}
