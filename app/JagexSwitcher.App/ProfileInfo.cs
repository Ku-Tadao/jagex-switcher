namespace JagexSwitcher.App;

internal sealed record ProfileInfo(
    string Name,
    string DisplayName,
    string ImportedAt,
    string LastPlayedAt = "",
    string CredentialStatus = "")
{
    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1].ToUpperInvariant();
    public bool IsReady => CredentialStatus == "Ready";
    public override string ToString() => $"{Name}, {DisplayName}, {CredentialStatus}";
}
