using System;
using System.IO;
using System.Text.Json;

namespace JioTv.Plugin.Auth;

/// <summary>
/// Persists JioCredentials as JSON in the plugin data directory.
/// The field names match JioTV Go (see JioCredentials) so the on-disk format
/// is mutually readable with JioTV Go's own credentials file.
/// </summary>
public class CredentialStore
{
    private readonly string _dataDirectory;
    private readonly string _filePath;

    /// <summary>
    /// Initializes a new instance of the <see cref="CredentialStore"/> class.
    /// </summary>
    /// <param name="dataDirectory">Directory holding jiotv_credentials.json.</param>
    public CredentialStore(string dataDirectory)
    {
        _filePath = Path.Combine(dataDirectory, "jiotv_credentials.json");
        _dataDirectory = dataDirectory;
    }

    /// <summary>Gets the full path of the credentials file (also used by tests).</summary>
    public string CredentialsFilePath => _filePath;

    /// <summary>Writes the given credentials to <see cref="CredentialsFilePath"/>.</summary>
    /// <param name="credentials">Credentials object to persist.</param>
    public void Save(JioCredentials credentials)
    {
        Directory.CreateDirectory(_dataDirectory);
        var json = JsonSerializer.Serialize(credentials);
        File.WriteAllText(_filePath, json);
    }

    /// <summary>Gets credentials from <see cref="CredentialsFilePath"/>, or null if missing.</summary>
    public JioCredentials? Load()
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        var json = File.ReadAllText(_filePath);
        return JsonSerializer.Deserialize<JioCredentials>(json);
    }

    /// <summary>Deletes <see cref="CredentialsFilePath"/> (logout).</summary>
    public void Clear()
    {
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
    }
}
