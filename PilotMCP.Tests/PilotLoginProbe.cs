using Ascon.Pilot.Common.DataProtection;
using Ascon.Pilot.DataClasses;
using Ascon.Pilot.Server.Api;
using Ascon.Pilot.Server.Api.Contracts;
using Ascon.Pilot.Transport;

namespace PilotMCP.Tests;

/// <summary>
/// Прямой вход на сервер Pilot под указанными логином/паролем — чтобы проверить,
/// что созданный или изменённый инструментами пароль действительно применился.
/// </summary>
internal static class PilotLoginProbe
{
    public static bool CanLogin(string login, string password)
    {
        var serverUrl = Environment.GetEnvironmentVariable("PILOT_SERVER_URL")
            ?? throw new InvalidOperationException("PILOT_SERVER_URL не задан.");
        var database = Environment.GetEnvironmentVariable("PILOT_DATABASE") ?? "";
        var licenseStr = Environment.GetEnvironmentVariable("PILOT_LICENSE_TYPE");
        if (!int.TryParse(licenseStr, out var licenseType)) licenseType = 100;

        var url = serverUrl;
        if (!string.IsNullOrWhiteSpace(database))
        {
            var uri = new Uri(serverUrl);
            if (string.IsNullOrWhiteSpace(uri.AbsolutePath) || uri.AbsolutePath == "/")
                url = serverUrl.TrimEnd('/') + "/" + database;
        }

        HttpPilotClient? client = null;
        try
        {
            var credentials = ConnectionCredentials.GetConnectionCredentials(
                url, login, password.ConvertToSecureString());

            client = new HttpPilotClient(credentials.GetConnectionString(), credentials.GetConnectionProxy());
            client.Connect(false);
            var auth = client.GetAuthenticationApi();
            auth.Login(
                string.IsNullOrWhiteSpace(database) ? credentials.DatabaseName : database,
                credentials.Username, credentials.ProtectedPassword, false, licenseType);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LoginProbe] {login}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            try { client?.Disconnect(); } catch { }
            try { (client as IDisposable)?.Dispose(); } catch { }
        }
    }

    public static void AssertCanLogin(string login, string password)
    {
        Assert.True(CanLogin(login, password), $"Не удалось войти под '{login}' с ожидаемым паролем.");
    }
}
