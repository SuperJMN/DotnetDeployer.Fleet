using DotnetDeployer.Fleet.Tooling.Agents;

namespace DotnetDeployer.Fleet.Tests;

public sealed class FleetClientConfigurationTests
{
    private static readonly Dictionary<string, string?> DesktopSettings = new()
    {
        ["endpoint"] = "http://192.168.1.29:5000",
        ["token"] = "stored-token",
        ["username"] = "jmn",
        ["password"] = "stored-password"
    };

    [Fact]
    public void The_desktop_client_settings_are_reused_when_nothing_else_is_given()
    {
        var credentials = FleetCredentialResolver.Resolve(null, null, _ => null, DesktopSettings);

        credentials.Should().Be(new FleetCredentials("http://192.168.1.29:5000", "stored-token", "jmn", "stored-password", "settings"));
    }

    [Fact]
    public void Environment_variables_override_the_stored_session()
    {
        var environment = new Dictionary<string, string> { ["FLEET_URL"] = "http://ci:5000", ["FLEET_TOKEN"] = "agent-token" };

        var credentials = FleetCredentialResolver.Resolve(null, null, environment.GetValueOrDefault, DesktopSettings);

        credentials.Should().Be(new FleetCredentials("http://ci:5000", "agent-token", null, null, "environment"));
    }

    [Fact]
    public void Stored_credentials_are_not_sent_to_a_different_coordinator()
    {
        var credentials = FleetCredentialResolver.Resolve("http://elsewhere:5000", null, _ => null, DesktopSettings);

        credentials.Token.Should().BeNull();
        credentials.Password.Should().BeNull();
    }

    [Theory]
    [InlineData("90s", 90)]
    [InlineData("30m", 1800)]
    [InlineData("2h", 7200)]
    [InlineData("45", 45)]
    [InlineData("00:10:00", 600)]
    public void Wait_timeouts_accept_common_duration_forms(string text, int seconds)
    {
        AgentCli.ParseDuration(text).Should().Be(TimeSpan.FromSeconds(seconds));
    }

    [Fact]
    public void Secret_values_come_from_stdin_or_an_environment_variable_but_not_both()
    {
        AgentCli.ReadSecretValue(null, true, new StringReader("s3cr3t\n")).Should().Be("s3cr3t");

        var both = () => AgentCli.ReadSecretValue("VAR", true, new StringReader("x"));
        var neither = () => AgentCli.ReadSecretValue(null, false, new StringReader("x"));
        both.Should().Throw<FleetUsageException>();
        neither.Should().Throw<FleetUsageException>();
    }
}
