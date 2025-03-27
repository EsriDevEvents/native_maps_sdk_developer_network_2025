using System.Windows.Media;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Symbology;
using Esri.ArcGISRuntime.UI;
using Esri.ArcGISRuntime.UtilityNetworks;

namespace ElectricUtilityEditor;

/// <summary>
/// For caching symbol watches
/// </summary>
public class SwatchHelper
{
    private static readonly SwatchHelper s_singleInstance = new();

    public static SwatchHelper Current
    {
        get { return s_singleInstance; }
    }

    private readonly Dictionary<string, IReadOnlyList<LegendInfo>> _legendCache = [];
    private readonly Dictionary<string, ImageSource?> _symbolCache = [];

    public async Task<ImageSource?> GetSwatchAsync(UtilityElement element)
    {
        var symbolKey = $"{element.NetworkSource.Name}-{element.AssetGroup.Name}";
        if (_symbolCache.ContainsKey(symbolKey))
        {
            return _symbolCache[symbolKey];
        }

        IReadOnlyList<LegendInfo>? legendInfos = null;
        if (_legendCache.ContainsKey(element.NetworkSource.Name))
        {
            legendInfos = _legendCache[element.NetworkSource.Name];
        }
        else if (element.NetworkSource.FeatureTable.Layer is Layer layer)
        {
            legendInfos = await layer.GetLegendInfosAsync();
        }

        if (
            legendInfos?.FirstOrDefault(i => i.Name == element.AssetGroup.Name)
                is not LegendInfo info
            || info.Symbol is null
        )
        {
            return null;
        }

        var swatch = await info.Symbol.CreateSwatchAsync();
        var source = await swatch.ToImageSourceAsync();
        _symbolCache[symbolKey] = source;
        return source;
    }

    public async Task<ImageSource?> GetSwatchAsync(
        Esri.ArcGISRuntime.UI.Graphic graphic,
        UtilityAssociationType associationType
    )
    {
        if (Enum.GetName(typeof(UtilityAssociationType), associationType) is not string symbolKey)
        {
            return null;
        }

        if (_symbolCache.ContainsKey(symbolKey))
        {
            return _symbolCache[symbolKey];
        }

        if (graphic.GraphicsOverlay?.Renderer?.GetSymbol(graphic) is not Symbol symbol)
        {
            return null;
        }

        var swatch = await symbol.CreateSwatchAsync();
        var source = await swatch.ToImageSourceAsync();
        _symbolCache[symbolKey] = source;
        return source;
    }
}
