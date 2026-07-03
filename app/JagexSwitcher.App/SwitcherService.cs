using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace JagexSwitcher.App;

internal sealed class SwitcherService
{
    private const string CaptureFlag = "--insecure-write-credentials";
    private const string CloudflaredDownloadUrl = "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";
    private const string EncryptedCredentialsPrefix = "JagexSwitcher-DPAPI:v1\n";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        WriteIndented = true
    };

    private readonly string vaultRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "jagex-account-switcher");

    private readonly string liveCreds = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".runelite",
        "credentials.properties");

    private readonly string runeLiteExe = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RuneLite",
        "RuneLite.exe");

    private readonly string runeLiteSettings = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RuneLite",
        "settings.json");

    private readonly string jagexLauncherExe = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        "Jagex Launcher",
        "JagexLauncher.exe");

    private string CredsDir => Path.Combine(vaultRoot, "credentials");
    private string ToolsDir => Path.Combine(vaultRoot, "tools");
    private string ProfilesFile => Path.Combine(vaultRoot, "profiles.json");

    public string GetVaultRoot() => vaultRoot;

    public IReadOnlyList<ProfileInfo> GetProfiles()
    {
        var data = ReadProfiles();

        return data.Profiles
            .OrderBy(profile => profile.Key, StringComparer.OrdinalIgnoreCase)
            .Select(profile => new ProfileInfo(
                profile.Key,
                profile.Value.DisplayName ?? "",
                profile.Value.ImportedAt ?? "",
                profile.Value.LastPlayedAt ?? "",
                GetCredentialStatus(profile.Key)))
            .ToList();
    }

    public string Import(string profileName)
    {
        if (!File.Exists(liveCreds))
        {
            throw new InvalidOperationException($"No live credentials at {liveCreds}. Log in via Jagex Launcher -> RuneLite first.");
        }

        var credentials = File.ReadAllText(liveCreds);
        var displayName = GetJxDisplayNameFromText(credentials);
        var data = ReadProfiles();
        var resolvedName = !string.IsNullOrWhiteSpace(profileName) && data.Profiles.ContainsKey(profileName)
            ? data.Profiles.Keys.First(key => string.Equals(key, profileName, StringComparison.OrdinalIgnoreCase))
            : ResolveAvailableProfileName(string.IsNullOrWhiteSpace(profileName) ? displayName ?? "" : profileName);
        SaveProfileCredentials(resolvedName, credentials);
        return resolvedName;
    }

    public string CompleteAddAccount(string profileName)
    {
        var importedName = Import(profileName);
        SetCaptureEnabled(false);

        if (File.Exists(liveCreds))
        {
            File.Delete(liveCreds);
        }

        return importedName;
    }

    public void Play(string profileName)
    {
        AssertProfileName(profileName);

        var data = ReadProfiles();
        if (!data.Profiles.ContainsKey(profileName))
        {
            throw new InvalidOperationException($"Unknown profile '{profileName}'.");
        }

        var vaultPath = GetVaultCredPath(profileName);
        if (!File.Exists(vaultPath))
        {
            throw new InvalidOperationException($"Vault credentials missing for '{profileName}'. Re-import after logging in.");
        }

        StartRuneLite(ParseJxEnvironment(ReadCredentialLines(vaultPath)));
        data.Profiles[profileName].LastPlayedAt = DateTimeOffset.Now.ToString("o");
        SaveProfiles(data);
    }

    public void RenameProfile(string oldName, string newName)
    {
        AssertProfileName(oldName);
        AssertProfileName(newName);

        var data = ReadProfiles();
        if (!data.Profiles.TryGetValue(oldName, out var metadata))
        {
            throw new InvalidOperationException($"Unknown profile '{oldName}'.");
        }

        if (!string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase) && data.Profiles.ContainsKey(newName))
        {
            throw new InvalidOperationException($"Profile '{newName}' already exists.");
        }

        var oldPath = GetVaultCredPath(oldName);
        var newPath = GetVaultCredPath(newName);
        if (File.Exists(newPath) && !string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Vault credentials already exist for '{newName}'.");
        }

        if (File.Exists(oldPath) && !string.Equals(oldPath, newPath, StringComparison.Ordinal))
        {
            Directory.CreateDirectory(CredsDir);
            if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
            {
                var tempPath = oldPath + ".rename";
                File.Move(oldPath, tempPath, overwrite: true);
                File.Move(tempPath, newPath, overwrite: true);
            }
            else
            {
                File.Move(oldPath, newPath);
            }
        }

        data.Profiles.Remove(oldName);
        data.Profiles[newName] = metadata;
        SaveProfiles(data);
    }

    public string ResolveAvailableProfileName(string desiredName)
    {
        return ResolveAvailableProfileName(
            desiredName,
            ReadProfiles().Profiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    public void Remove(string profileName)
    {
        AssertProfileName(profileName);

        var data = ReadProfiles();
        if (!data.Profiles.Remove(profileName))
        {
            throw new InvalidOperationException($"Unknown profile '{profileName}'.");
        }

        var vaultPath = GetVaultCredPath(profileName);
        if (File.Exists(vaultPath))
        {
            File.Delete(vaultPath);
        }

        SaveProfiles(data);
    }

    public void ForgetAllProfiles()
    {
        if (Directory.Exists(CredsDir))
        {
            Directory.Delete(CredsDir, recursive: true);
        }

        if (File.Exists(ProfilesFile))
        {
            File.Delete(ProfilesFile);
        }
    }

    public async Task<PairTransferSession> StartPairTransferAsync(string profileName)
    {
        AssertProfileName(profileName);

        var data = ReadProfiles();
        if (!data.Profiles.ContainsKey(profileName))
        {
            throw new InvalidOperationException($"Unknown profile '{profileName}'.");
        }

        var vaultPath = GetVaultCredPath(profileName);
        if (!File.Exists(vaultPath))
        {
            throw new InvalidOperationException($"Vault credentials missing for '{profileName}'. Re-import after logging in.");
        }

        var credentials = ReadVaultCredentials(vaultPath);
        var displayName = GetJxDisplayNameFromText(credentials);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new InvalidOperationException($"Vault credentials for '{profileName}' have no JX_DISPLAY_NAME=. Re-import this profile.");
        }

        var port = GetFreeTcpPort();
        var cloudflaredPath = await EnsureCloudflaredAsync();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var session = new PairTransferSession(listener, port, cloudflaredPath, new PairTransferPackage
        {
            ProfileName = profileName,
            DisplayName = displayName,
            Credentials = credentials
        }, CreatePairCode());

        try
        {
            await session.StartAsync();
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public async Task<string> ReceivePairAsync(string tunnelUrl, string code, string profileName)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("Pair code is required.");
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var request = JsonSerializer.Serialize(new PairRequest { Code = code.Trim() }, JsonOptions);
        using var response = await client.PostAsync(
            PairEndpointFromUrl(tunnelUrl),
            new StringContent(request, Encoding.UTF8, "application/json"));

        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(body)
                ? $"Pair transfer failed: {(int)response.StatusCode} {response.ReasonPhrase}"
                : body.Trim());
        }

        var package = JsonSerializer.Deserialize<PairTransferPackage>(body, JsonOptions)
            ?? throw new InvalidOperationException("Pair transfer returned an empty response.");

        if (string.IsNullOrWhiteSpace(package.Credentials))
        {
            throw new InvalidOperationException("Pair transfer returned no credentials.");
        }

        var importedName = ResolveAvailableProfileName(string.IsNullOrWhiteSpace(profileName)
            ? string.IsNullOrWhiteSpace(package.DisplayName) ? package.ProfileName : package.DisplayName
            : profileName);
        if (string.IsNullOrWhiteSpace(importedName))
        {
            throw new InvalidOperationException("Pair transfer returned no profile name. Enter a profile name and try again.");
        }

        SaveProfileCredentials(importedName, package.Credentials);
        return importedName;
    }

    public string PrepareLogin()
    {
        if (File.Exists(liveCreds))
        {
            File.Delete(liveCreds);
            return "Cleared live credentials.properties. Jagex Launcher session unchanged.";
        }

        return "No live credentials.properties to clear.";
    }

    public void BeginAddAccount()
    {
        SetCaptureEnabled(true);
        PrepareLogin();
        StartJagexLauncherRuneLite();
    }

    public bool HasCapturedCredentials()
    {
        if (!File.Exists(liveCreds))
        {
            return false;
        }

        try
        {
            _ = ParseJxEnvironment(File.ReadAllLines(liveCreds));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public bool IsJagexLauncherRunning()
    {
        return IsProcessRunning("JagexLauncher");
    }

    public bool IsRuneLiteRunning()
    {
        return IsProcessRunning("RuneLite");
    }

    public bool IsOfficialOldSchoolClientRunning()
    {
        return IsProcessRunning("osclient");
    }

    public bool IsCaptureEnabled()
    {
        var settings = ReadRuneLiteSettings();
        return GetClientArguments(settings).Contains(CaptureFlag, StringComparer.Ordinal);
    }

    public void SetCaptureEnabled(bool enabled)
    {
        var settings = ReadRuneLiteSettings();
        var args = GetClientArguments(settings)
            .Where(arg => !string.Equals(arg, CaptureFlag, StringComparison.Ordinal))
            .ToList();

        if (enabled)
        {
            args.Add(CaptureFlag);
        }

        var array = new JsonArray();
        foreach (var arg in args)
        {
            array.Add(arg);
        }

        settings["clientArguments"] = array;
        SaveRuneLiteSettings(settings);
    }

    public void StartCaptureLogin()
    {
        SetCaptureEnabled(true);
        StartJagexLauncherRuneLite();
    }

    private void StartJagexLauncherRuneLite()
    {
        if (!File.Exists(jagexLauncherExe))
        {
            throw new InvalidOperationException($"Jagex Launcher not found at {jagexLauncherExe}");
        }

        StartProcess(jagexLauncherExe, "--launch=osrs_runelite");
    }

    private static bool IsProcessRunning(string name)
    {
        return Process.GetProcessesByName(name).Length > 0;
    }

    internal static void SelfCheck()
    {
        var env = ParseJxEnvironment(new[]
        {
            "JX_DISPLAY_NAME=SelfCheck",
            "JX_SESSION_ID=session",
            "JX_CHARACTER_ID=character",
            "JX_ACCESS_TOKEN=abc=123",
            "JX_REFRESH_TOKEN=refresh",
            "NOT_JX=ignored",
            "JX_EMPTY=",
            "# comment"
        });

        if (env.Count != 6 ||
            env["JX_DISPLAY_NAME"] != "SelfCheck" ||
            env["JX_ACCESS_TOKEN"] != "abc=123" ||
            env["JX_EMPTY"] != "")
        {
            throw new InvalidOperationException("JX environment parse self-check failed.");
        }

        _ = new JsonObject
        {
            ["clientArguments"] = new JsonArray(CaptureFlag)
        }.ToJsonString(JsonOptions);

        if (!Regex.IsMatch(CreatePairCode(), "^\\d{8}$", RegexOptions.CultureInvariant) ||
            PairEndpointFromUrl("https://example.trycloudflare.com").ToString() != "https://example.trycloudflare.com/pair")
        {
            throw new InvalidOperationException("Pair transfer self-check failed.");
        }

        var rejectedPlainHttp = false;
        try
        {
            _ = PairEndpointFromUrl("http://example.com");
        }
        catch (InvalidOperationException)
        {
            rejectedPlainHttp = true;
        }

        if (!rejectedPlainHttp ||
            PairEndpointFromUrl("http://127.0.0.1:9999").ToString() != "http://127.0.0.1:9999/pair")
        {
            throw new InvalidOperationException("Pair URL scheme self-check failed.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Main", "SelfCheck" };
        if (ResolveAvailableProfileName("Self Check", names) != "SelfCheck_2" ||
            ResolveAvailableProfileName("", names) != "Profile")
        {
            throw new InvalidOperationException("Profile name resolution self-check failed.");
        }

        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes("dpapi"), null, DataProtectionScope.CurrentUser);
        var unprotected = Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser));
        if (unprotected != "dpapi")
        {
            throw new InvalidOperationException("DPAPI self-check failed.");
        }
    }

    private void StartRuneLite(IReadOnlyDictionary<string, string> environment)
    {
        if (!File.Exists(runeLiteExe))
        {
            throw new InvalidOperationException($"RuneLite not found at {runeLiteExe}. Install from https://runelite.net");
        }

        StartProcess(runeLiteExe, null, environment);
    }

    private static void StartProcess(string fileName, string? arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments ?? "",
            UseShellExecute = environment is null
        };

        if (environment is not null)
        {
            foreach (var item in environment)
            {
                startInfo.Environment[item.Key] = item.Value;
            }
        }

        Process.Start(startInfo);
    }

    private async Task<string> EnsureCloudflaredAsync()
    {
        var localCloudflared = Path.Combine(AppContext.BaseDirectory, "cloudflared.exe");
        if (File.Exists(localCloudflared))
        {
            return localCloudflared;
        }

        var cachedCloudflared = Path.Combine(ToolsDir, "cloudflared.exe");
        if (File.Exists(cachedCloudflared))
        {
            return cachedCloudflared;
        }

        Directory.CreateDirectory(ToolsDir);
        var tempCloudflared = cachedCloudflared + ".download";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            await using var download = await client.GetStreamAsync(CloudflaredDownloadUrl);
            await using (var file = File.Create(tempCloudflared))
            {
                await download.CopyToAsync(file);
            }

            File.Move(tempCloudflared, cachedCloudflared, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempCloudflared))
            {
                File.Delete(tempCloudflared);
            }

            throw;
        }

        return cachedCloudflared;
    }

    private void SaveProfileCredentials(string profileName, string credentials)
    {
        AssertProfileName(profileName);

        var env = ParseJxEnvironment(ReadLines(credentials));
        var displayName = env["JX_DISPLAY_NAME"];
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new InvalidOperationException("Credentials have no JX_DISPLAY_NAME=. Re-login with --insecure-write-credentials enabled.");
        }

        Directory.CreateDirectory(CredsDir);
        WriteVaultCredentials(GetVaultCredPath(profileName), credentials);

        var data = ReadProfiles();
        data.Profiles.TryGetValue(profileName, out var existing);
        data.Profiles[profileName] = new ProfileMetadata
        {
            DisplayName = displayName,
            ImportedAt = DateTimeOffset.Now.ToString("o"),
            LastPlayedAt = existing?.LastPlayedAt
        };
        SaveProfiles(data);
    }

    private ProfilesData ReadProfiles()
    {
        if (!File.Exists(ProfilesFile))
        {
            return new ProfilesData();
        }

        var raw = File.ReadAllText(ProfilesFile);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new ProfilesData();
        }

        var data = JsonSerializer.Deserialize<ProfilesData>(raw) ?? new ProfilesData();
        data.Profiles = new Dictionary<string, ProfileMetadata>(data.Profiles, StringComparer.OrdinalIgnoreCase);
        return data;
    }

    private void SaveProfiles(ProfilesData data)
    {
        Directory.CreateDirectory(vaultRoot);

        var ordered = data.Profiles
            .OrderBy(profile => profile.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(profile => profile.Key, profile => profile.Value, StringComparer.OrdinalIgnoreCase);

        var output = new ProfilesData { Profiles = ordered };
        WriteAllTextAtomic(ProfilesFile, JsonSerializer.Serialize(output, JsonOptions));
    }

    private JsonObject ReadRuneLiteSettings()
    {
        JsonObject settings;
        if (File.Exists(runeLiteSettings))
        {
            settings = JsonNode.Parse(File.ReadAllText(runeLiteSettings)) as JsonObject ?? new JsonObject();
        }
        else
        {
            settings = new JsonObject();
        }

        if (settings["clientArguments"] is not JsonArray)
        {
            settings["clientArguments"] = new JsonArray();
        }

        if (settings["jvmArguments"] is null)
        {
            settings["jvmArguments"] = new JsonArray();
        }

        return settings;
    }

    private void SaveRuneLiteSettings(JsonObject settings)
    {
        var dir = Path.GetDirectoryName(runeLiteSettings);
        if (string.IsNullOrWhiteSpace(dir))
        {
            throw new InvalidOperationException("Could not resolve RuneLite settings directory.");
        }

        Directory.CreateDirectory(dir);
        WriteAllTextAtomic(runeLiteSettings, settings.ToJsonString(JsonOptions));
    }

    private static void WriteAllTextAtomic(string path, string content)
    {
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, path, overwrite: true);
    }

    private static List<string> GetClientArguments(JsonObject settings)
    {
        var result = new List<string>();
        if (settings["clientArguments"] is not JsonArray args)
        {
            return result;
        }

        foreach (var node in args)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var arg) && !string.IsNullOrWhiteSpace(arg))
            {
                result.Add(arg);
            }
        }

        return result;
    }

    private string GetVaultCredPath(string profileName)
    {
        return Path.Combine(CredsDir, $"{profileName}.properties");
    }

    private string GetCredentialStatus(string profileName)
    {
        var vaultPath = GetVaultCredPath(profileName);
        if (!File.Exists(vaultPath))
        {
            return "Missing credentials";
        }

        try
        {
            _ = ParseJxEnvironment(ReadCredentialLines(vaultPath));
            return "Ready";
        }
        catch
        {
            return "Needs re-import";
        }
    }

    private static IEnumerable<string> ReadCredentialLines(string path)
    {
        return ReadLines(ReadVaultCredentials(path));
    }

    private static IEnumerable<string> ReadLines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static string ReadVaultCredentials(string path)
    {
        var raw = File.ReadAllText(path);
        if (!raw.StartsWith(EncryptedCredentialsPrefix, StringComparison.Ordinal))
        {
            return raw;
        }

        var protectedBytes = Convert.FromBase64String(raw[EncryptedCredentialsPrefix.Length..].Trim());
        var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void WriteVaultCredentials(string path, string credentials)
    {
        var bytes = Encoding.UTF8.GetBytes(credentials);
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        WriteAllTextAtomic(path, EncryptedCredentialsPrefix + Convert.ToBase64String(protectedBytes));
    }

    private static string? GetJxDisplayNameFromText(string credentials)
    {
        using var reader = new StringReader(credentials);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("JX_DISPLAY_NAME=", StringComparison.Ordinal))
            {
                return line["JX_DISPLAY_NAME=".Length..].Trim();
            }
        }

        return null;
    }

    private static string CreatePairCode()
    {
        return RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8", CultureInfo.InvariantCulture);
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static Uri PairEndpointFromUrl(string tunnelUrl)
    {
        if (!Uri.TryCreate(tunnelUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException("Enter the pair URL from the sending PC.");
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            throw new InvalidOperationException("Pair URL must use https.");
        }

        return new UriBuilder(uri)
        {
            Path = "pair",
            Query = "",
            Fragment = ""
        }.Uri;
    }

    private static Dictionary<string, string> ParseJxEnvironment(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (!line.StartsWith("JX_", StringComparison.Ordinal))
            {
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals < 1)
            {
                continue;
            }

            result[line[..equals]] = line[(equals + 1)..];
        }

        var required = new[]
        {
            "JX_DISPLAY_NAME",
            "JX_SESSION_ID",
            "JX_CHARACTER_ID",
            "JX_ACCESS_TOKEN",
            "JX_REFRESH_TOKEN"
        };
        var missing = required.Where(key => !result.ContainsKey(key)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"Vault credentials missing {string.Join(", ", missing)}. Re-import this profile.");
        }

        return result;
    }

    private static string ResolveAvailableProfileName(string desiredName, ISet<string> existing)
    {
        var baseName = Regex.Replace(desiredName.Trim(), "[^A-Za-z0-9_-]+", "");
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "Profile";
        }

        var name = baseName;
        for (var suffix = 2; existing.Contains(name); suffix++)
        {
            name = $"{baseName}_{suffix}";
        }

        return name;
    }

    private static void AssertProfileName(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            throw new InvalidOperationException("Profile name is required.");
        }

        if (!Regex.IsMatch(profileName, "^[A-Za-z0-9_-]+$"))
        {
            throw new InvalidOperationException("Invalid profile name. Use letters, numbers, hyphen, or underscore only.");
        }
    }

    private sealed class ProfilesData
    {
        [JsonPropertyName("profiles")]
        public Dictionary<string, ProfileMetadata> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ProfileMetadata
    {
        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("importedAt")]
        public string? ImportedAt { get; set; }

        [JsonPropertyName("lastPlayedAt")]
        public string? LastPlayedAt { get; set; }
    }

    private sealed class PairRequest
    {
        [JsonPropertyName("code")]
        public string Code { get; set; } = "";
    }

    internal sealed class PairTransferPackage
    {
        [JsonPropertyName("profileName")]
        public string ProfileName { get; set; } = "";

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = "";

        [JsonPropertyName("credentials")]
        public string Credentials { get; set; } = "";
    }

    internal sealed class PairTransferSession : IDisposable
    {
        private const int MaxFailedAttempts = 5;
        private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(15);

        private static readonly Regex TunnelUrlPattern = new(
            "https://[a-z0-9-]+\\.trycloudflare\\.com",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly HttpListener listener;
        private readonly int port;
        private readonly string cloudflaredPath;
        private readonly PairTransferPackage package;
        private readonly CancellationTokenSource stop = new();
        private Process? cloudflared;
        private int disposed;
        private int failedAttempts;

        public PairTransferSession(HttpListener listener, int port, string cloudflaredPath, PairTransferPackage package, string code)
        {
            this.listener = listener;
            this.port = port;
            this.cloudflaredPath = cloudflaredPath;
            this.package = package;
            Code = code;
        }

        public string Code { get; }
        public string TunnelUrl { get; private set; } = "";
        public event Action<string>? Closed;

        public async Task StartAsync()
        {
            _ = Task.Run(ServeAsync);

            cloudflared = StartCloudflared(cloudflaredPath, port);
            TunnelUrl = await WaitForTunnelUrlAsync(cloudflared, stop.Token);

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(SessionTtl, stop.Token);
                    Close("Pair transfer expired after 15 minutes. Create a new pair if you still need it.");
                }
                catch (OperationCanceledException)
                {
                }
            });
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 1)
            {
                return;
            }

            stop.Cancel();
            listener.Close();

            try
            {
                if (cloudflared is { HasExited: false })
                {
                    cloudflared.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best effort cleanup; the transfer is already closed.
            }

            cloudflared?.Dispose();
            stop.Dispose();
        }

        private void Close(string reason)
        {
            if (disposed == 1)
            {
                return;
            }

            Dispose();
            Closed?.Invoke(reason);
        }

        private static Process StartCloudflared(string cloudflaredPath, int port)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = cloudflaredPath,
                Arguments = $"tunnel --url http://127.0.0.1:{port}",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            try
            {
                return Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Could not start cloudflared.");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Could not start cloudflared. Try deleting %APPDATA%\\jagex-account-switcher\\tools\\cloudflared.exe so the app can download a fresh copy.", ex);
            }
        }

        private static async Task<string> WaitForTunnelUrlAsync(Process process, CancellationToken cancellationToken)
        {
            var found = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            void ReadLine(string? line)
            {
                if (line is null)
                {
                    return;
                }

                var match = TunnelUrlPattern.Match(line);
                if (match.Success)
                {
                    found.TrySetResult(match.Value);
                }
            }

            process.OutputDataReceived += (_, args) => ReadLine(args.Data);
            process.ErrorDataReceived += (_, args) => ReadLine(args.Data);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                if (!found.Task.IsCompleted)
                {
                    found.TrySetException(new InvalidOperationException("cloudflared exited before creating a tunnel URL."));
                }
            };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var completed = await Task.WhenAny(found.Task, Task.Delay(TimeSpan.FromSeconds(30), cancellationToken));
            if (completed != found.Task)
            {
                throw new InvalidOperationException("Timed out waiting for cloudflared to create a trycloudflare URL.");
            }

            return await found.Task;
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var context = await listener.GetContextAsync().WaitAsync(stop.Token);
                    _ = Task.Run(() => HandleContext(context));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (HttpListenerException)
            {
            }
        }

        private void HandleContext(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            if (request.HttpMethod == "GET")
            {
                WriteHttp(response, 200, "Pair transfer is ready. Use Jagex Switcher Receive Pair with the pairing code.", "text/plain; charset=utf-8");
                return;
            }

            if (request.HttpMethod != "POST" || request.Url?.AbsolutePath != "/pair")
            {
                WriteHttp(response, 404, "Not found.", "text/plain; charset=utf-8");
                return;
            }

            if (request.ContentLength64 <= 0 || request.ContentLength64 > 2048)
            {
                WriteHttp(response, 400, "Bad pair request.", "text/plain; charset=utf-8");
                return;
            }

            using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
            var body = reader.ReadToEnd();
            PairRequest? pairRequest;
            try
            {
                pairRequest = JsonSerializer.Deserialize<PairRequest>(body, JsonOptions);
            }
            catch (JsonException)
            {
                WriteHttp(response, 400, "Bad pair request.", "text/plain; charset=utf-8");
                return;
            }

            if (!CodeMatches(pairRequest?.Code))
            {
                var attempts = Interlocked.Increment(ref failedAttempts);
                if (attempts >= MaxFailedAttempts)
                {
                    WriteHttp(response, 403, "Too many failed attempts. Pair share closed.", "text/plain; charset=utf-8");
                    Close("Pair share closed after too many failed code attempts.");
                    return;
                }

                Thread.Sleep(750);
                WriteHttp(response, 403, "Pair code did not match.", "text/plain; charset=utf-8");
                return;
            }

            WriteHttp(response, 200, JsonSerializer.Serialize(package, JsonOptions), "application/json; charset=utf-8");
            Close("Pair transfer completed.");
        }

        private bool CodeMatches(string? candidate)
        {
            var expected = Encoding.UTF8.GetBytes(Code);
            var actual = Encoding.UTF8.GetBytes(candidate?.Trim() ?? "");
            return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
        }

        private static void WriteHttp(HttpListenerResponse response, int statusCode, string body, string contentType)
        {
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            response.StatusCode = statusCode;
            response.ContentType = contentType;
            response.ContentLength64 = bodyBytes.Length;
            response.OutputStream.Write(bodyBytes);
            response.Close();
        }
    }
}
