using System;
using JioTv.Plugin.Auth;
using Xunit;

namespace JioTv.Tests;

/// <summary>
/// CredentialStore persistence round-trips and failure modes.
/// </summary>
public class CredentialStoreTests : IDisposable
{
    private readonly string _dataDir = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "jiotv-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (System.IO.Directory.Exists(_dataDir))
        {
            System.IO.Directory.Delete(_dataDir, true);
        }
    }

    [Fact]
    public void SaveLoad_RoundTrips()
    {
        var store = new CredentialStore(_dataDir);

        store.Save(new JioCredentials
        {
            AccessToken = "at",
            RefreshToken = "rt",
            SSOToken = "sso",
            CRM = "12345",
            UniqueId = "uni",
            DeviceId = "dev",
            LastTokenRefreshTime = "1700000000",
        });

        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal("at", loaded!.AccessToken);
        Assert.Equal("rt", loaded.RefreshToken);
        Assert.Equal("sso", loaded.SSOToken);
        Assert.Equal("12345", loaded.CRM);
        Assert.Equal("uni", loaded.UniqueId);
        Assert.Equal("1700000000", loaded.LastTokenRefreshTime);
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        var store = new CredentialStore(_dataDir);
        Assert.Null(store.Load());
    }

    [Fact]
    public void Clear_RemovesStoredCredentials()
    {
        var store = new CredentialStore(_dataDir);
        store.Save(new JioCredentials { AccessToken = "at" });
        store.Clear();
        Assert.Null(store.Load());
    }

    [Fact]
    public void Save_UsesGoCompatibleJsonFieldNames()
    {
        var store = new CredentialStore(_dataDir);
        store.Save(new JioCredentials { AccessToken = "at", CRM = "crm", UniqueId = "uid" });

        var json = System.IO.File.ReadAllText(store.CredentialsFilePath);
        // Go types.go JSON tags: accessToken, crm, uniqueId, refreshToken, ssoToken
        Assert.Contains("\"accessToken\":\"at\"", json);
        Assert.Contains("\"crm\":\"crm\"", json);
        Assert.Contains("\"uniqueId\":\"uid\"", json);
    }
}
