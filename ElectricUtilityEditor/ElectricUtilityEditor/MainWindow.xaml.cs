using System.Collections.ObjectModel;
using System.Drawing;
using System.Windows;
using Esri.ArcGISRuntime;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Symbology;
using Esri.ArcGISRuntime.UI;
using Esri.ArcGISRuntime.UI.Controls;
using Esri.ArcGISRuntime.UtilityNetworks;
using Geometry = Esri.ArcGISRuntime.Geometry.Geometry;
using ImageSource = System.Windows.Media.ImageSource;

namespace ElectricUtilityEditor;

#region HELPER

internal record struct PortalAccess(string Host, string Url, string Username, string Password);

internal record struct RuntimeAccess(string License, string AdvancedEditingExtension);

internal record struct SymbolizedElement(UtilityElement Element, ImageSource? Swatch);

internal record struct SymbolizedGraphic(Graphic Graphic, ImageSource? Swatch);

internal record struct SymbolizedAssociation(
    UtilityAssociation Association,
    SymbolizedGraphic Graphic,
    SymbolizedElement FromElement,
    SymbolizedElement ToElement
);

#endregion HELPER

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    #region PRIVATE MEMBERS

    private const string VECTOR_TILE_PACKAGE = @"C:\dev\NapervilleElectricMap\p13\Naperville.vtpk";
    private const string SYNC_ENABLED_GEODATABASE =
        @"C:\dev\NapervilleElectricMap\p13\gonxr3mc.rju.geodatabase";

    private const string GLOBALID = "GLOBALID";

    private const string ELECTRIC = "Electric";
    private const string TIER_NAME = "Electric Distribution";
    private const string STARTING_GLOBALID = "5FA67541-4A18-4AE1-A666-A98400ECCFEE";
    private const string STARTING_TERMINAL = "CB:Line Side";

    private const string ASSOCIATIONS = "ASSOCIATIONS";
    private const string STARTING_POINTS = "STARTING_POINTS";
    private const string EDIT_AREA = "EDITAREA";

    private const double BUFFER_DISTANCE = 0.50;
    private const double VIEWPOINT_PADDING = 50;

    private readonly Symbol ConnectivitySymbol = new SimpleLineSymbol(
        SimpleLineSymbolStyle.Dot,
        Color.Blue,
        5d
    );

    private readonly Symbol StartingSymbol = new SimpleMarkerSymbol(
        SimpleMarkerSymbolStyle.Cross,
        Color.Green,
        20d
    );
    private readonly Symbol EditAreaSymbol = new SimpleLineSymbol(
        SimpleLineSymbolStyle.Solid,
        Color.Red,
        2d
    );
    private readonly Viewpoint InitialViewpoint = new Viewpoint(
        new MapPoint(-9814273.21056671, 5127739.502049677, SpatialReferences.WebMercator),
        6230
    );

    private readonly Viewpoint FirstEditViewpoint = new Viewpoint(
        new MapPoint(-9813315.839928849, 5127134.405696829, SpatialReferences.WebMercator),
        78
    );

    private readonly Viewpoint NextEditViewpoint = new Viewpoint(
        new MapPoint(-9819094.392271247, 5127679.83318855, SpatialReferences.WebMercator),
        187
    );

    bool _hasVisitedFirstViewpoint = false;
    private UtilityTraceParameters? _traceParameters;
    private ObservableCollection<UtilityAssociation> _associations = [];

    private UtilityAssociation? _toDelete;
    private UtilityAssociation? _toAdd;
    private Graphic? _associationGraphic;
    #endregion PRIVATE MEMBERS

    public MainWindow()
    {
        InitializeComponent();
        SetupGraphicsOverlays();
        _ = InitializeAsync();
    }

    #region SENSITIVE

    static MainWindow()
    {
        ArcGISRuntimeEnvironment.SetLicense(
            RUNTIME_ACCESS.License,
            [RUNTIME_ACCESS.AdvancedEditingExtension]
        );
        ArcGISRuntimeEnvironment.Initialize();
    }

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

    #region HELPER METHODS

    // For displaying associations and default starting location for trace
    private void SetupGraphicsOverlays()
    {
        MyMapView.GraphicsOverlays ??= new GraphicsOverlayCollection();
        MyMapView.GraphicsOverlays.Add(
            new GraphicsOverlay()
            {
                Id = ASSOCIATIONS,
                Renderer = new SimpleRenderer(ConnectivitySymbol),
            }
        );
        MyMapView.GraphicsOverlays.Add(
            new GraphicsOverlay()
            {
                Id = STARTING_POINTS,
                Renderer = new SimpleRenderer(StartingSymbol),
            }
        );
        MyMapView.GraphicsOverlays.Add(
            new GraphicsOverlay() { Id = EDIT_AREA, Renderer = new SimpleRenderer(EditAreaSymbol) }
        );
    }

    // Loads offline map content from a vector tile package and geodatabase containing a utility network
    private async Task LoadOfflineMapAsync()
    {
        var tileCache = await VectorTileCache.CreateAsync(VECTOR_TILE_PACKAGE);

        var map = new Map(new Basemap(new ArcGISVectorTiledLayer(tileCache)))
        {
            InitialViewpoint = InitialViewpoint,
        };
        await map.LoadAsync();

        var gdb = await Geodatabase.OpenAsync(SYNC_ENABLED_GEODATABASE);

        var utilityNetwork = gdb.UtilityNetworks.Single();
        map.UtilityNetworks.Add(utilityNetwork);
        await utilityNetwork.LoadAsync();

        ArgumentNullException.ThrowIfNull(utilityNetwork.Definition);

        foreach (
            var networkSource in utilityNetwork.Definition.NetworkSources.Where(ns =>
                ns.SourceType == UtilityNetworkSourceType.Edge
            )
        )
        {
            if (
                networkSource.SourceUsageType == UtilityNetworkSourceUsageType.SubnetLine
                || networkSource.SourceUsageType == UtilityNetworkSourceUsageType.EdgeObject
                || networkSource.SourceUsageType
                    == UtilityNetworkSourceUsageType.StructureEdgeObject
            )
                continue;
            var layer = new FeatureLayer(networkSource.FeatureTable);
            map.OperationalLayers.Add(layer);
        }

        foreach (
            var networkSource in utilityNetwork.Definition.NetworkSources.Where(ns =>
                ns.SourceType == UtilityNetworkSourceType.Junction
            )
        )
        {
            if (
                networkSource.SourceUsageType == UtilityNetworkSourceUsageType.JunctionObject
                || networkSource.SourceUsageType
                    == UtilityNetworkSourceUsageType.StructureJunctionObject
            )
                continue;

            var layer = new FeatureLayer(networkSource.FeatureTable);
            map.OperationalLayers.Add(layer);
        }

        if (utilityNetwork.DirtyAreaTable is not null)
        {
            var layer = new FeatureLayer(utilityNetwork.DirtyAreaTable)
            {
                DefinitionExpression = "ERRORCODE == 0",
            };
            map.OperationalLayers.Add(layer);
        }

        MyMapView.Map = map;
    }

    // Sets up the default subnetwork trace from a circuit breaker to demonstrate changes in associations
    // can cause a change in resource flow
    private async Task SetupTraceAsync()
    {
        if (
            MyMapView.Map?.UtilityNetworks.Single() is not UtilityNetwork utilityNetwork
            || MyMapView.GraphicsOverlays?.Single(g => g.Id == ASSOCIATIONS)
                is not GraphicsOverlay associationsOverlay
            || MyMapView.GraphicsOverlays?.Single(g => g.Id == STARTING_POINTS)
                is not GraphicsOverlay startingOverlay
        )
            return;

        ArgumentNullException.ThrowIfNull(utilityNetwork.Definition);

        var electricDomainNetwork = utilityNetwork.Definition.DomainNetworks.Single(d =>
            d.Name == ELECTRIC
        );
        var subtransmissionTier = electricDomainNetwork.Tiers.Single(t => t.Name == TIER_NAME);
        var deviceNetworkSource = electricDomainNetwork.NetworkSources.Single(n =>
            n.SourceUsageType == UtilityNetworkSourceUsageType.Device
        );

        var result = await deviceNetworkSource.FeatureTable.QueryFeaturesAsync(
            new QueryParameters { WhereClause = $"{GLOBALID} = '{{{STARTING_GLOBALID}}}'" }
        );

        var feature = result?.Single() as ArcGISFeature;
        ArgumentNullException.ThrowIfNull(feature);

        startingOverlay.Graphics.Add(new Graphic(feature.Geometry));

        var startingElement = utilityNetwork.CreateElement(feature);
        startingElement.Terminal =
            startingElement.AssetType.TerminalConfiguration?.Terminals.Single(t =>
                t.Name == STARTING_TERMINAL
            );

        _traceParameters = new UtilityTraceParameters(
            UtilityTraceType.Subnetwork,
            [startingElement]
        );
        _traceParameters.TraceConfiguration = subtransmissionTier.GetDefaultTraceConfiguration();

        await TraceAsync();
    }

    // Displays connectivity associations for the utility network extent
    private async Task SetupAssociationsAsync()
    {
        if (
            MyMapView.Map?.UtilityNetworks.SingleOrDefault() is not UtilityNetwork utilityNetwork
            || MyMapView.GraphicsOverlays?.SingleOrDefault(g => g.Id == ASSOCIATIONS)
                is not GraphicsOverlay associationsOverlay
        )
            return;

        ArgumentNullException.ThrowIfNull(utilityNetwork.Definition);
        var associations = await utilityNetwork.GetAssociationsAsync(
            utilityNetwork.Definition.Extent,
            UtilityAssociationType.Connectivity
        );
        foreach (var association in associations)
        {
            if (association.Geometry is null)
                continue;
            _associations.Add(association);
            var associationGraphic = new Graphic(association.Geometry);
            associationGraphic.Attributes[GLOBALID] = association.GlobalId;
            associationsOverlay.Graphics.Add(associationGraphic);
        }

        await TraceAsync();
    }

    // Runs trace after every edit
    private async Task TraceAsync()
    {
        try
        {
            if (
                MyMapView.Map?.UtilityNetworks.Single() is not UtilityNetwork utilityNetwork
                || _traceParameters is null
            )
                return;

            foreach (var layer in MyMapView.Map.OperationalLayers.OfType<FeatureLayer>())
            {
                layer.ClearSelection();
            }

            var traceResult = await utilityNetwork.TraceAsync(_traceParameters);
            var elementResult = traceResult.OfType<UtilityElementTraceResult>().Single();
            var resultExtents = new List<Envelope>();
            foreach (var featureLayer in MyMapView.Map.OperationalLayers.OfType<FeatureLayer>())
            {
                var elements = elementResult.Elements.Where(element =>
                    element.NetworkSource.FeatureTable == featureLayer.FeatureTable
                );
                if (!elements.Any())
                    continue;

                var features = await utilityNetwork.GetFeaturesForElementsAsync(elements);
                featureLayer.SelectFeatures(features);
            }
            AssociationGrid.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ErrorType.Text = $"Trace failed with: {ex.GetType().Name}";
            ErrorMessage.Text = ex.Message;
            ErrorGroup.Visibility = Visibility.Visible;
        }
    }

    // Initializes the map with association graphics and feature selection from a trace
    private async Task InitializeAsync()
    {
        await LoadOfflineMapAsync();
        await SetupAssociationsAsync();
        await SetupTraceAsync();
    }

    // Displays association for update or deletion
    private async Task DisplayAssociationInfo(UtilityAssociation association)
    {
        if (_associationGraphic is null || _toDelete is null)
            return;

        var swatch = await SwatchHelper.Current.GetSwatchAsync(association.FromElement);
        var from = new SymbolizedElement(association.FromElement, swatch);

        swatch = await SwatchHelper.Current.GetSwatchAsync(association.ToElement);
        var to = new SymbolizedElement(association.ToElement, swatch);

        swatch = await SwatchHelper.Current.GetSwatchAsync(
            _associationGraphic,
            association.AssociationType
        );
        var graphic = new SymbolizedGraphic(_associationGraphic, swatch);

        AssociationGrid.DataContext = new SymbolizedAssociation(_toDelete, graphic, from, to);
        AssociationGrid.Visibility = Visibility.Visible;
    }

    // Ensures cached associations and graphics are in sync with the association edits
    private async Task UpdateCachedAssociationsAsync(
        UtilityAssociation toDelete,
        UtilityAssociation? toAdd = null
    )
    {
        if (toDelete is null)
            return;

        _associations.Remove(toDelete);

        if (
            toAdd is not null
            && MyMapView.Map?.UtilityNetworks.Single() is UtilityNetwork utilityNetwork
        )
        {
            var associations = await utilityNetwork.GetAssociationsAsync(
                toDelete.FromElement,
                toDelete.AssociationType
            );

            if (
                _associationGraphic is not null
                && associations.FirstOrDefault(a =>
                    a.AssociationType == toDelete.AssociationType
                    && a.FromElement.GlobalId == toDelete.FromElement.GlobalId
                    && a.ToElement.GlobalId == toDelete.ToElement.GlobalId
                )
                    is UtilityAssociation newAssociation
            )
            {
                _associationGraphic.Attributes[GLOBALID] = newAssociation.GlobalId;
                _associations.Add(newAssociation);
            }
        }
        else if (
            _associationGraphic is not null
            && _associationGraphic.GraphicsOverlay is GraphicsOverlay associationOverlay
        )
        {
            _associationGraphic.GraphicsOverlay.Graphics.Remove(_associationGraphic);
        }
    }

    private void OnDismissError(object sender, RoutedEventArgs e)
    {
        ErrorGroup.Visibility = Visibility.Collapsed;
    }

    // Zooms to two areas with interesting associations
    private void OnZoomToEditArea(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var viewpoint = _hasVisitedFirstViewpoint ? NextEditViewpoint : FirstEditViewpoint;
        _hasVisitedFirstViewpoint = !_hasVisitedFirstViewpoint;
        MyMapView.SetViewpointAsync(viewpoint);
    }

    #endregion HELPER METHODS

    // Identifies an association graphic for editing
    private async void OnGeoViewTapped(object sender, GeoViewInputEventArgs e)
    {
        if (
            MyMapView.Map?.UtilityNetworks.Single() is not UtilityNetwork utilityNetwork
            || MyMapView.GraphicsOverlays?.Single(g => g.Id == ASSOCIATIONS)
                is not GraphicsOverlay associationsOverlay
        )
            return;
        try
        {
            AssociationGrid.Visibility = Visibility.Collapsed;

            var result = await MyMapView.IdentifyGraphicsOverlayAsync(
                associationsOverlay,
                e.Position,
                5,
                false
            );
            _associationGraphic = result.GeoElements.FirstOrDefault() as Graphic;
            if (
                _associationGraphic is null
                || _associationGraphic.Attributes[GLOBALID] is not Guid globalId
                || _associations.SingleOrDefault(a => a.GlobalId.Equals(globalId) == true)
                    is not UtilityAssociation association
            )
                return;

            _toDelete = association;

            await DisplayAssociationInfo(association);
        }
        catch (Exception ex)
        {
            ErrorType.Text = $"Identify association failed with: {ex.GetType().Name}";
            ErrorMessage.Text = ex.Message;
            ErrorGroup.Visibility = Visibility.Visible;
        }
    }

    // Validates the dirty area using the updated association's geometry
    private async Task ValidateAsync(bool withAdd = false)
    {
        if (
            MyMapView.Map?.UtilityNetworks.Single() is not UtilityNetwork utilityNetwork
            || MyMapView.GraphicsOverlays?.Single(g => g.Id == EDIT_AREA)
                is not GraphicsOverlay editOverlay
        )
            return;
        if (
            _toDelete?.Geometry?.Buffer(BUFFER_DISTANCE) is Geometry bufferedGeometry
            && bufferedGeometry.Extent is Envelope extent
        )
        {
            editOverlay.Graphics.Clear();
            editOverlay.Graphics.Add(new Graphic(extent));

            await UpdateCachedAssociationsAsync(_toDelete, withAdd ? _toAdd : null);

            await MyMapView.SetViewpointGeometryAsync(extent, VIEWPOINT_PADDING);

            // For visualization purpose only to show dirty area gets created and resolved
            // await Task.Delay(1000);

            var job = utilityNetwork.ValidateNetworkTopology(extent);
            await job.GetResultAsync();

            editOverlay.Graphics.Clear();
        }
    }

    // Updates properties by creating a new association based on the old association.
    // Determines whether add is allowed before deleting the old association.
    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        if (
            MyMapView.Map?.UtilityNetworks.Single() is not UtilityNetwork utilityNetwork
            || utilityNetwork.Geodatabase is not Geodatabase gdb
            || _toDelete is null
            || !utilityNetwork.CanDeleteAssociations()
        )
            return;
        try
        {
            if (!gdb.IsInTransaction)
                gdb.BeginTransaction();

            _toAdd = new UtilityAssociation(
                _toDelete.AssociationType,
                _toDelete.FromElement, // changed terminal property
                _toDelete.ToElement
            );

            var canAdd = await utilityNetwork.CanAddAssociationAsync(_toAdd);

            if (canAdd)
                await utilityNetwork.DeleteAssociationAsync(_toDelete);

            await utilityNetwork.AddAssociationAsync(_toAdd);

            await ValidateAsync(withAdd: true);

            await TraceAsync();
        }
        catch (Exception ex)
        {
            ErrorType.Text = $"Update failed: {ex.GetType().Name}";
            ErrorMessage.Text = ex.Message;
            ErrorGroup.Visibility = Visibility.Visible;
        }
    }

    // Deletes the association with check for delete capability
    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (
            MyMapView.Map?.UtilityNetworks.Single() is not UtilityNetwork utilityNetwork
            || utilityNetwork.Geodatabase is not Geodatabase gdb
            || _toDelete is null
            || !utilityNetwork.CanDeleteAssociations()
        )
            return;
        try
        {
            if (!gdb.IsInTransaction)
                gdb.BeginTransaction();

            await utilityNetwork.DeleteAssociationAsync(_toDelete);

            await ValidateAsync();

            await TraceAsync();
        }
        catch (Exception ex)
        {
            ErrorType.Text = $"Delete failed: {ex.GetType().Name}";
            ErrorMessage.Text = ex.Message;
            ErrorGroup.Visibility = Visibility.Visible;
        }
    }
}
