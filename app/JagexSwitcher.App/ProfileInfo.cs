using Avalonia;
using Avalonia.Media;

namespace JagexSwitcher.App;

internal sealed record ProfileInfo(
    string Name,
    string DisplayName,
    string ImportedAt,
    string LastPlayedAt = "",
    string CredentialStatus = "")
{
    // Muted jewel tones, dark enough for a white initial.
    private static readonly IBrush[] Avatars = new[]
    {
        ("#C9973F", "#8A5E1E"), ("#2F9E8F", "#1B6B73"), ("#C2554D", "#8A2F3B"),
        ("#7C6BD6", "#4B3E9E"), ("#6E9A4A", "#3F6B36"), ("#5B86C4", "#34578F")
    }.Select(pair => (IBrush)new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse(pair.Item1), 0), new GradientStop(Color.Parse(pair.Item2), 1) }
    }).ToArray();

    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1].ToUpperInvariant();
    public bool IsReady => CredentialStatus == "Ready";
    public string Subtitle => !IsReady ? CredentialStatus : string.IsNullOrWhiteSpace(DisplayName) ? "Ready to play" : DisplayName;
    public DateTimeOffset? LastPlayed => DateTimeOffset.TryParse(LastPlayedAt, out var time) ? time : null;
    public string LastPlayedShort => LastPlayed is { } time ? Ago(time) : "";
    // Stable per name (string.GetHashCode is randomised per process).
    public IBrush AvatarBrush => Avatars[(Name.Aggregate(0, (h, c) => h * 31 + char.ToUpperInvariant(c)) & int.MaxValue) % Avatars.Length];
    public override string ToString() => $"{Name}, {DisplayName}, {CredentialStatus}";

    internal static string Ago(DateTimeOffset time)
    {
        var span = DateTimeOffset.Now - time;
        return span.TotalMinutes < 1 ? "Just now" : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m ago" :
            span.TotalHours < 24 ? $"{(int)span.TotalHours}h ago" : span.TotalDays < 2 ? "Yesterday" :
            span.TotalDays < 30 ? $"{(int)span.TotalDays}d ago" : time.ToLocalTime().ToString("dd MMM yyyy");
    }
}
