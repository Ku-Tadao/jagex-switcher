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
    private string ProfilesFile => Path.Combine(vaultRoot, "profiles.json");

    public IReadOnlyList<ProfileInfo> GetProfiles()
    {
        var data = ReadProfiles();

        return data.Profiles
            .OrderBy(profile => profile.Key, StringComparer.OrdinalIgnoreCase)
            .Select(profile => new ProfileInfo(
                profile.Key,
                profile.Value.DisplayName ?? "",
                profile.Value.ImportedAt ?? ""))
            .ToList();
    }

    public void Import(string profileName)
    {
        AssertProfileName(profileName);

        if (!File.Exists(liveCreds))
        {
            throw new InvalidOperationException($"No live credentials at {liveCreds}. Log in via Jagex Launcher -> RuneLite first.");
        }

        SaveProfileCredentials(profileName, File.ReadAllText(liveCreds));
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

        StartRuneLite(ParseJxEnvironment(File.ReadLines(vaultPath)));
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

        var credentials = File.ReadAllText(vaultPath);
        var displayName = GetJxDisplayNameFromText(credentials);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new InvalidOperationException($"Vault credentials for '{profileName}' have no JX_DISPLAY_NAME=. Re-import this profile.");
        }

        var port = GetFreeTcpPort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var session = new PairTransferSession(listener, port, new PairTransferPackage
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

        var importedName = string.IsNullOrWhiteSpace(profileName) ? package.ProfileName?.Trim() : profileName.Trim();
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
        return File.Exists(liveCreds) && !string.IsNullOrWhiteSpace(GetJxDisplayName(liveCreds));
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
            "JX_ACCESS_TOKEN=abc=123",
            "NOT_JX=ignored",
            "JX_EMPTY=",
            "# comment"
        });

        if (env.Count != 3 ||
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

    private void SaveProfileCredentials(string profileName, string credentials)
    {
        AssertProfileName(profileName);

        var displayName = GetJxDisplayNameFromText(credentials);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new InvalidOperationException("Credentials have no JX_DISPLAY_NAME=. Re-login with --insecure-write-credentials enabled.");
        }

        Directory.CreateDirectory(CredsDir);
        File.WriteAllText(GetVaultCredPath(profileName), credentials);

        var data = ReadProfiles();
        data.Profiles[profileName] = new ProfileMetadata
        {
            DisplayName = displayName,
            ImportedAt = DateTimeOffset.Now.ToString("o")
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
        File.WriteAllText(ProfilesFile, JsonSerializer.Serialize(output, JsonOptions));
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
        File.WriteAllText(runeLiteSettings, settings.ToJsonString(JsonOptions));
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

    private static string? GetJxDisplayName(string path)
    {
        return GetJxDisplayNameFromText(File.ReadAllText(path));
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

        if (!result.ContainsKey("JX_DISPLAY_NAME"))
        {
            throw new InvalidOperationException("Vault credentials file has no JX_DISPLAY_NAME=. Re-import this profile.");
        }

        return result;
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
        private static readonly Regex TunnelUrlPattern = new(
            "https://[a-z0-9-]+\\.trycloudflare\\.com",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly HttpListener listener;
        private readonly int port;
        private readonly PairTransferPackage package;
        private readonly CancellationTokenSource stop = new();
        private Process? cloudflared;
        private int disposed;

        public PairTransferSession(HttpListener listener, int port, PairTransferPackage package, string code)
        {
            this.listener = listener;
            this.port = port;
            this.package = package;
            Code = code;
        }

        public string Code { get; }
        public string TunnelUrl { get; private set; } = "";
        public event Action? Completed;

        public async Task StartAsync()
        {
            _ = Task.Run(ServeAsync);

            cloudflared = StartCloudflared(port);
            TunnelUrl = await WaitForTunnelUrlAsync(cloudflared, stop.Token);
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

        private static Process StartCloudflared(int port)
        {
            var localCloudflared = Path.Combine(AppContext.BaseDirectory, "cloudflared.exe");
            var startInfo = new ProcessStartInfo
            {
                FileName = File.Exists(localCloudflared) ? localCloudflared : "cloudflared",
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
                throw new InvalidOperationException("cloudflared.exe was not found. Install Cloudflare Tunnel, or put cloudflared.exe next to JagexSwitcher.exe.", ex);
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

            if (!string.Equals(pairRequest?.Code, Code, StringComparison.Ordinal))
            {
                WriteHttp(response, 403, "Pair code did not match.", "text/plain; charset=utf-8");
                return;
            }

            WriteHttp(response, 200, JsonSerializer.Serialize(package, JsonOptions), "application/json; charset=utf-8");
            Completed?.Invoke();
            Dispose();
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
