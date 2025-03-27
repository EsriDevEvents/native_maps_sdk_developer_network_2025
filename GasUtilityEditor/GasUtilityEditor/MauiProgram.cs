using System.Globalization;
using CommunityToolkit.Mvvm.Messaging.Messages;
using Esri.ArcGISRuntime;
using Esri.ArcGISRuntime.Maui;
using Esri.ArcGISRuntime.Security;
using Esri.Calcite.Maui;
using Microsoft.Extensions.Logging;

namespace GasUtilityEditor;

// For alternating the grid row color in attribute viewer
internal class ColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (
            parameter is CollectionView listView
            && listView.ItemsSource is Dictionary<string, string> source
            && value is KeyValuePair<string, string> item
        )
        {
            var index = source.ToList().IndexOf(item);
            return index % 2 == 0 ? Colors.LightGray : Colors.WhiteSmoke;
        }
        return value;
    }

    public object? ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture
    )
    {
        throw new NotImplementedException();
    }
}

// For changing the visibility of branch version name
internal class DisplayBranchMessage(bool value) : ValueChangedMessage<bool>(value) { }

internal record struct PortalAccess(
    string Host,
    string Url,
    string Username,
    string Password
);

internal record struct RuntimeAccess(string License, string AdvancedEditingExtension);

public static class MauiProgram
{
    #region SENSITIVE
    internal static PortalAccess PORTAL_ACCESS =
        new(
            "<HOSTNAME>",
            "https://<HOSTNAME>/portal/sharing/rest",
            "<USERNAME>",
            "<PASSWORD>"
        );

    private static RuntimeAccess RUNTIME_ACCESS =
        new(
            "<LICENSEKEY>",
            "<ADVANCEDEDITINGEXTENSIONKEY>"
        );
    #endregion SENSITIVE

    private static PortalChallengeHandler PORTAL_CHALLENGE_HANDLER = new();

    private class PortalChallengeHandler : IChallengeHandler
    {
        public async Task<Credential> CreateCredentialAsync(CredentialRequestInfo requestInfo)
        {
            var credential = await AccessTokenCredential.CreateAsync(
                new Uri(PORTAL_ACCESS.Url),
                PORTAL_ACCESS.Username,
                PORTAL_ACCESS.Password
            );
            return credential;
        }
    }

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            })
            .UseArcGISRuntime(config =>
                config
                    .UseLicense(RUNTIME_ACCESS.License, [RUNTIME_ACCESS.AdvancedEditingExtension])
                    .ConfigureAuthentication(authConfig =>
                        authConfig.UseChallengeHandler(PORTAL_CHALLENGE_HANDLER)
                    )
            )
            .UseCalcite();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
