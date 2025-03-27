using Esri.ArcGISRuntime.Mapping;

namespace GasUtilityEditor;

internal static class EnumerableExtensions
{
    // Flattens the list of layers
    internal static IEnumerable<FeatureLayer> ToFeatureLayers(this IEnumerable<Layer> layers)
    {
        foreach (var layer in layers)
        {
            if (layer is FeatureLayer featureLayer)
            {
                yield return featureLayer;
            }

            if (layer is GroupLayer groupLayer)
            {
                foreach (var childFeatureLayer in groupLayer.Layers.ToFeatureLayers())
                {
                    yield return childFeatureLayer;
                }
            }
        }
    }
}
