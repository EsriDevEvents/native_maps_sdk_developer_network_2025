using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Esri.ArcGISRuntime.UtilityNetworks;

namespace ElectricUtilityEditor;

internal class VisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var property = parameter?.ToString() ?? string.Empty;
        if (value is UtilityAssociation association)
        {
            if (property == nameof(UtilityAssociation.FractionAlongEdge))
                return
                    association.AssociationType
                    == UtilityAssociationType.JunctionEdgeObjectConnectivityMidspan
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            if (property == nameof(UtilityAssociation.IsContainmentVisible))
                return association.AssociationType == UtilityAssociationType.Containment
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }
        else if (value is UtilityElement element && property == "Terminal")
        {
            return (element.AssetType.TerminalConfiguration?.Terminals.Count > 0)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

internal class IsEnabledConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var property = parameter?.ToString() ?? string.Empty;
        if (value is UtilityElement element && property == "Terminal")
        {
            return (element.AssetType.TerminalConfiguration?.Terminals.Count > 1);
        }
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
