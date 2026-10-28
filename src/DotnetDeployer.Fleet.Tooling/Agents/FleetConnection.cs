using System.Net;
using System.Text.Json;
using DotnetDeployer.Fleet.Api.Client;

namespace DotnetDeployer.Fleet.Tooling.Agents;

/// <summary>Where the coordinator URL and credentials come from, in order of precedence.</summary>
public sealed record FleetCredentials(string? Url, string? Token, string? Username, string? Password, string Source);

/// <summary>
/// The settings file the desktop client already keeps (<c>~/.config/DotnetFleet/settings.json</c>
/// on Linux). The CLI reads it so an agent needs no extra configuration, and <c>fleet login</c>
/// writes to it. Unknown keys are preserved.
/// </summary>
public sealed class FleetSettingsFile(string path)
{
    public static string DefaultPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DotnetFleet", "settings.json");

    public string Path { get; } = path;

    public Dictionary<string, string?> Load()
    {
        try
        {
            return File.Exists(Path)
                ? JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(Path)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void SaveSession(string url, string token, string username)
    {
        var data = Load();
        data["endpoint"] = url;
        data["token"] = token;
        data["username"] = username;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(data));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public void SaveToken(string token)
    {
        var data = Load();
        data["token"] = token;
        File.WriteAllText(Path, JsonSerializer.Serialize(data));
    }
}

public static class FleetCredentialResolver
{
    public const string UrlVariable = "FLEET_URL";
    public const string TokenVariable = "FLEET_TOKEN";

    /// <summary>
    /// Command-line options win, then <c>FLEET_URL</c>/<c>FLEET_TOKEN</c>, then the desktop
    /// client's settings file. A URL given without a token still reuses the stored credentials
    /// when they belong to the same coordinator.
    /// </summary>
    public static FleetCredentials Resolve(
        string? explicitUrl,
        string? explicitToken,
        Func<string, string?> environment,
        IReadOnlyDictionary<string, string?> settings)
    {
        var storedUrl = settings.GetValueOrDefault("endpoint");
        var envUrl = environment(UrlVariable);
        var envToken = environment(TokenVariable);

        var url = FirstNonEmpty(explicitUrl, envUrl, storedUrl);
        var sameAsStored = url is not null && storedUrl is not null && SameUrl(url, storedUrl);

        if (!string.IsNullOrWhiteSpace(explicitToken))
            return new FleetCredentials(url, explicitToken, null, null, "option");
        if (!string.IsNullOrWhiteSpace(envToken))
            return new FleetCredentials(url, envToken, null, null, "environment");
        if (sameAsStored)
        {
            return new FleetCredentials(
                url,
                settings.GetValueOrDefault("token"),
                settings.GetValueOrDefault("username"),
                settings.GetValueOrDefault("password"),
                "settings");
        }

        return new FleetCredentials(url, null, null, null, "none");
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static bool SameUrl(string a, string b) =>
        string.Equals(a.Trim().TrimEnd('/'), b.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Opens an authenticated <see cref="FleetApiClient"/>. When the stored token has expired and the
/// settings file also holds the desktop client's saved credentials, it signs in again and stores
/// the fresh token, as the desktop client does.
/// </summary>
public sealed class FleetSession(FleetCredentials credentials, FleetSettingsFile? settingsFile, HttpMessageHandler? handler = null)
{
    public FleetCredentials Credentials { get; } = credentials;

    public async Task<FleetApiClient> ConnectAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(Credentials.Url))
            throw new FleetUsageException(
                $"No coordinator configured. Run 'fleet login --url <coordinator>', set {FleetCredentialResolver.UrlVariable}, or pass --url.");

        var httpHandler = handler ?? new HttpClientHandler();
        var client = new FleetApiClient(httpHandler, httpHandler);
        client.SetBaseAddress(Credentials.Url);

        if (!string.IsNullOrWhiteSpace(Credentials.Token))
        {
            client.SetToken(Credentials.Token);
            try
            {
                await client.GetSession();
                return client;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized && CanSignIn)
            {
                client.ClearToken();
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new FleetUsageException($"The {Credentials.Source} token was rejected (expired or issued by another coordinator). Run 'fleet login'.");
            }
        }

        if (!CanSignIn)
            throw new FleetUsageException("Not signed in. Run 'fleet login', set FLEET_TOKEN, or pass --token.");

        var login = await SignInAsync(client, Credentials.Username!, Credentials.Password!);
        if (Credentials.Source == "settings")
            settingsFile?.SaveToken(login.Token);
        return client;
    }

    private bool CanSignIn =>
        !string.IsNullOrWhiteSpace(Credentials.Username) && !string.IsNullOrWhiteSpace(Credentials.Password);

    public static async Task<FleetApiClient.LoginResponse> SignInAsync(FleetApiClient client, string username, string password)
    {
        try
        {
            var login = await client.LoginAsync(username, password)
                        ?? throw new FleetUsageException("The coordinator returned an empty login response.");
            client.SetToken(login.Token);
            return login;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new FleetUsageException($"Sign-in failed for user '{username}': wrong username or password.");
        }
    }
}
