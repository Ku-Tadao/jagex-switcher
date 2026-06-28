namespace JagexSwitcher.App;

internal sealed record ProfileInfo(
    string Name,
    string DisplayName,
    string ImportedAt,
    string LastPlayedAt = "",
    string CredentialStatus = "");
