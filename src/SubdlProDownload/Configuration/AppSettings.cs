using System.IO;
using System.Text.Json;

namespace SubdlProDownload.Configuration;

public sealed record AppSettings(string ApiKey)
{
    private const string CredentialFileName = "subdl-pro-download-credentials.json";

    public static AppSettings LoadSaved()
    {
        var credentialPath = Path.Combine(AppContext.BaseDirectory, CredentialFileName);
        if (!File.Exists(credentialPath)) return new AppSettings(string.Empty);
        try
        {
            var stored = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(credentialPath));
            return stored ?? new AppSettings(string.Empty);
        }
        catch (JsonException)
        {
            return new AppSettings(string.Empty);
        }
    }

    public void Save()
    {
        var credentialPath = Path.Combine(AppContext.BaseDirectory, CredentialFileName);
        File.WriteAllText(credentialPath, JsonSerializer.Serialize(this));
    }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);
}
