using System.IO;
using System.Text.Json;

namespace DotnetDeployer.Fleet.App.ViewModels;

public interface ISettingsService
{
    string? GetEndpoint();
    void SetEndpoint(string? endpoint);
    string? GetToken();
    void SetToken(string? token);
    void ClearToken();
    LoginCredentials? GetCredentials();
    void SetCredentials(string username, string password);
    void ClearCredentials();
    AppTheme GetTheme();
    void SetTheme(AppTheme theme);
}

public sealed record LoginCredentials(string Username, string Password);

public class InMemorySettingsService : ISettingsService
{
    private string? _endpoint;
    private string? _token;
    private LoginCredentials? credentials;
    private AppTheme theme;

    public string? GetEndpoint() => _endpoint;
    public void SetEndpoint(string? endpoint) => _endpoint = endpoint;
    public string? GetToken() => _token;
    public void SetToken(string? token) => _token = token;
    public void ClearToken() => _token = null;
    public LoginCredentials? GetCredentials() => credentials;
    public void SetCredentials(string username, string password) => credentials = new LoginCredentials(username, password);
    public void ClearCredentials() => credentials = null;
    public AppTheme GetTheme() => theme;
    public void SetTheme(AppTheme value) => theme = value;
}

public class FileSettingsService : ISettingsService
{
    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DotnetFleet", "settings.json");

    private Dictionary<string, string?> _data = [];

    public FileSettingsService() => Load();

    public string? GetEndpoint() => _data.GetValueOrDefault("endpoint");
    public void SetEndpoint(string? value) { _data["endpoint"] = value; Save(); }
    public string? GetToken() => _data.GetValueOrDefault("token");
    public void SetToken(string? value) { _data["token"] = value; Save(); }
    public void ClearToken() { _data.Remove("token"); Save(); }
    public LoginCredentials? GetCredentials()
    {
        var username = _data.GetValueOrDefault("username");
        var password = _data.GetValueOrDefault("password");
        return string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)
            ? null
            : new LoginCredentials(username, password);
    }

    public void SetCredentials(string username, string password)
    {
        _data["username"] = username;
        _data["password"] = password;
        Save();
    }

    public void ClearCredentials()
    {
        _data.Remove("username");
        _data.Remove("password");
        Save();
    }

    public AppTheme GetTheme() =>
        Enum.TryParse<AppTheme>(_data.GetValueOrDefault("theme"), out var theme) ? theme : AppTheme.System;

    public void SetTheme(AppTheme value) { _data["theme"] = value.ToString(); Save(); }

    private void Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
                _data = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(SettingsFile)) ?? [];
        }
        catch { /* ignore corrupt settings */ }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(_data));
    }
}
