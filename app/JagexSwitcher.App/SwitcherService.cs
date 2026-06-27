using System.Diagnostics;
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

        var displayName = GetJxDisplayName(liveCreds);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new InvalidOperationException("Live credentials file has no JX_DISPLAY_NAME=. Re-login with --insecure-write-credentials enabled.");
        }

        Directory.CreateDirectory(CredsDir);
        File.Copy(liveCreds, GetVaultCredPath(profileName), overwrite: true);

        var data = ReadProfiles();
        data.Profiles[profileName] = new ProfileMetadata
        {
            DisplayName = displayName,
            ImportedAt = DateTimeOffset.Now.ToString("o")
        };
        SaveProfiles(data);
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
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("JX_DISPLAY_NAME=", StringComparison.Ordinal))
            {
                return line["JX_DISPLAY_NAME=".Length..].Trim();
            }
        }

        return null;
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
}
