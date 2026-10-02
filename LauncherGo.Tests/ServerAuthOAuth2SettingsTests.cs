using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class ServerAuthOAuth2SettingsTests
{
    [Fact]
    public async Task DeployAuthMod_UsesMetadataNameAndIdAndRemovesOldCopy()
    {
        var directory = Directory.CreateTempSubdirectory("launchergo-auth-deploy-");
        try
        {
            var profile = new InstanceProfile { DirectoryPath = directory.FullName };
            var modsPath = Directory.CreateDirectory(Path.Combine(directory.FullName, "Mods"));
            var oldPath = Directory.CreateDirectory(Path.Combine(modsPath.FullName, "launchergoauth"));
            await File.WriteAllTextAsync(Path.Combine(oldPath.FullName, "modinfo.json"),
                """{"modid":"launchergoauth","name":"ServerAuth","version":"1.1.0"}""");
            var configService = new UnusedServerConfigService();
            var auth = new ServerAuthService(configService);

            await auth.EnsureAuthModDeployedAsync(profile);

            Assert.False(Directory.Exists(oldPath.FullName));
            var installed = Assert.Single(await new InstanceModService(configService).GetModsAsync(profile));
            Assert.Equal("launchergoauth", installed.ModId);
            Assert.Equal("ServerAuth", installed.Name);
            Assert.Equal("1.1.0", installed.Version);
            Assert.Equal("ServerAuth-1.1.0", Path.GetFileName(installed.FilePath));
            Assert.True(await auth.GetAuthModEnabledAsync(profile));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task DeployAuthMod_DoesNotOverwriteDifferentModWithSameFolderName()
    {
        var directory = Directory.CreateTempSubdirectory("launchergo-auth-collision-");
        try
        {
            var profile = new InstanceProfile { DirectoryPath = directory.FullName };
            var occupied = Directory.CreateDirectory(Path.Combine(directory.FullName, "Mods", "ServerAuth-1.1.0"));
            var infoPath = Path.Combine(occupied.FullName, "modinfo.json");
            var info = """{"modid":"other","name":"ServerAuth","version":"1.1.0"}""";
            await File.WriteAllTextAsync(infoPath, info);

            var auth = new ServerAuthService(new UnusedServerConfigService());
            await Assert.ThrowsAsync<InvalidOperationException>(() => auth.EnsureAuthModDeployedAsync(profile));
            Assert.Equal(info, await File.ReadAllTextAsync(infoPath));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task DeployAuthMod_RepairsOwnedFolderWithMissingModId()
    {
        var directory = Directory.CreateTempSubdirectory("launchergo-auth-repair-");
        try
        {
            var profile = new InstanceProfile { DirectoryPath = directory.FullName };
            var installed = Directory.CreateDirectory(Path.Combine(directory.FullName, "Mods", "ServerAuth-1.1.0"));
            await File.WriteAllTextAsync(Path.Combine(installed.FullName, "modinfo.json"),
                """{"name":"ServerAuth","version":"1.1.0"}""");
            await File.WriteAllTextAsync(Path.Combine(installed.FullName, "serverauth.dll"), "old");
            var configService = new UnusedServerConfigService();

            await new ServerAuthService(configService).EnsureAuthModDeployedAsync(profile);

            var mod = Assert.Single(await new InstanceModService(configService).GetModsAsync(profile));
            Assert.Equal("launchergoauth", mod.ModId);
            Assert.Equal("OK", mod.Status);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task SaveAndLoad_PreservesAndNormalizesOAuth2Settings()
    {
        var directory = Directory.CreateTempSubdirectory("launchergo-auth-");
        try
        {
            var profile = new InstanceProfile { DirectoryPath = directory.FullName };
            var service = new ServerAuthService(new UnusedServerConfigService());

            await service.SaveSettingsAsync(profile, new ServerAuthSettings
            {
                Enabled = true,
                OAuth2 = new ServerAuthOAuth2Settings
                {
                    Enabled = true,
                    DiscoveryUrl = " https://connect.example/.well-known/openid-configuration ",
                    ClientId = " client-id ",
                    ClientSecret = " client-secret ",
                    Scope = " ",
                    PublicCallbackBaseUrl = "https://auth.example/serverauth/oauth2/callback/",
                    ListenPrefix = "http://127.0.0.1:18092",
                    UserIdClaim = " ",
                    UsernameClaim = "account.username"
                }
            });

            var loaded = await service.LoadSettingsAsync(profile);

            Assert.True(loaded.OAuth2.Enabled);
            Assert.Equal("https://connect.example/.well-known/openid-configuration", loaded.OAuth2.DiscoveryUrl);
            Assert.Equal("client-id", loaded.OAuth2.ClientId);
            Assert.Equal("client-secret", loaded.OAuth2.ClientSecret);
            Assert.Equal("openid profile email", loaded.OAuth2.Scope);
            Assert.Equal(
                "https://auth.example/serverauth/oauth2/callback",
                loaded.OAuth2.PublicCallbackBaseUrl);
            Assert.Equal("http://127.0.0.1:18092/", loaded.OAuth2.ListenPrefix);
            Assert.Equal("sub", loaded.OAuth2.UserIdClaim);
            Assert.Equal("account.username", loaded.OAuth2.UsernameClaim);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task LoadLegacySettings_CreatesOAuth2Defaults()
    {
        var directory = Directory.CreateTempSubdirectory("launchergo-auth-");
        try
        {
            var configDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "ModConfig"));
            await File.WriteAllTextAsync(
                Path.Combine(configDirectory.FullName, "serverauth.json"),
                """
                {
                  "Enabled": true,
                  "Discourse": { "Enabled": false }
                }
                """);
            var profile = new InstanceProfile { DirectoryPath = directory.FullName };
            var service = new ServerAuthService(new UnusedServerConfigService());

            var loaded = await service.LoadSettingsAsync(profile);

            Assert.False(loaded.OAuth2.Enabled);
            Assert.Equal("openid profile email", loaded.OAuth2.Scope);
            Assert.Equal("sub", loaded.OAuth2.UserIdClaim);
            Assert.Equal("preferred_username", loaded.OAuth2.UsernameClaim);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private sealed class UnusedServerConfigService : IInstanceServerConfigService
    {
        public Task<ServerCommonSettings> LoadServerSettingsAsync(InstanceProfile profile, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorldSettings> LoadWorldSettingsAsync(InstanceProfile profile, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<WorldRuleValue>> LoadWorldRulesAsync(InstanceProfile profile, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SaveSettingsAsync(
            InstanceProfile profile,
            ServerCommonSettings serverSettings,
            WorldSettings worldSettings,
            IReadOnlyList<WorldRuleValue> rules,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> LoadRawJsonAsync(InstanceProfile profile, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SaveRawJsonAsync(InstanceProfile profile, string json, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ImportRawJsonAsync(InstanceProfile profile, string jsonFilePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
