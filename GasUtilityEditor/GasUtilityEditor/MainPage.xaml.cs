using System.Collections.ObjectModel;
using System.Data;
using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.Messaging;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Mapping.Popups;
using Esri.ArcGISRuntime.Maui;
using Esri.ArcGISRuntime.Portal;
using Esri.ArcGISRuntime.Symbology;
using Esri.ArcGISRuntime.Tasks.Offline;
using Esri.ArcGISRuntime.UI;
using Esri.ArcGISRuntime.UtilityNetworks;
using Color = System.Drawing.Color;
using Map = Esri.ArcGISRuntime.Mapping.Map;

namespace GasUtilityEditor
{
    [SupportedOSPlatform("windows10.0.19041")]
    [SupportedOSPlatform("android26.0")]
    [SupportedOSPlatform("iOS16.0")]
    [SupportedOSPlatform("maccatalyst16.1")]
    public partial class MainPage : ContentPage, IRecipient<DisplayBranchMessage>
    {
        #region PRIVATE MEMBERS
        private readonly string _localCachePath = Path.Combine(
            FileSystem.Current.CacheDirectory,
            "UtilityEditor"
        );

        private readonly SimpleLineSymbol _editAreaSymbol = new SimpleLineSymbol(
            SimpleLineSymbolStyle.Solid,
            Color.Red,
            2
        );

        private readonly SimpleMarkerSymbol _startingPointsSymbol = new SimpleMarkerSymbol(
            SimpleMarkerSymbolStyle.Cross,
            Color.Green,
            20d
        );

        private const string ASSETID_FIELD_NAME = "assetid";

        private const string NAMED_TRACE_ISOLATING_FEATURES_ONLY = "DevSummit-ValveIsolation";
        private const string NAMED_TRACE_ALL_ISOLATED_FEATURES = "DevSummit-Isolated";

        private const string OPERABLE_FIELD_NAME = "operable";
        private const string NOTES_FIELD_NAME = "notes";
        private const string NON_OPERABLE_CODE_NAME = "No";
        private const string OPERABLE_CODE_NAME = "Yes";

        private const string EDIT_AREA = "Edit Area";
        private const string STARTING_POINTS = "Starting Points";

        private readonly string[] _inoperableReasons =
        [
            "Paved over",
            "Unlocatable",
            "Unable to close",
        ];

        private readonly string[] _fieldNames =
        [
            ASSETID_FIELD_NAME,
            "assetgroup",
            "assettype",
            "pressuresubnetworkname",
            "devicestatus",
            "turnstoclose",
            OPERABLE_FIELD_NAME,
            NOTES_FIELD_NAME,
        ];

        private const double BUFFER_DISTANCE = 0.50;
        private const double LOCATE_BUFFER_DISTANCE = 100;
        private const double VIEWPOINT_PADDING = 50;

        private UtilityElement? _elementForPipelineToReplace;
        private ObservableCollection<Popup> _popupItemsSource = [];
        private string _branchVersionName = string.Empty;
        private string _previousStatus = string.Empty;

        private Map? _onlineMap;
        private Map? _offlineMap;
        private Popup? _current;

        private CancellationTokenSource? _cts = null;
        private OfflineMapTask? _offlineTask = null;
        private ObservableCollection<PortalItem> _webmapItems = [];
        private ObservableCollection<PreplannedMapArea> _mapAreaItems = [];
        private bool _isOnline = false;
        #endregion PRIVATE MEMBERS

        public MainPage()
        {
            InitializeComponent();

            #region INITIALIZATION
            WeakReferenceMessenger.Default.Register<DisplayBranchMessage>(this);

            InoperableReasons.ItemsSource = _inoperableReasons;
            WebmapPicker.ItemsSource = _webmapItems;
            MapAreaPicker.ItemsSource = _mapAreaItems;

            MyMapView.GraphicsOverlays ??= [];
            MyMapView.GraphicsOverlays.Add(
                new GraphicsOverlay()
                {
                    Id = EDIT_AREA,
                    Renderer = new SimpleRenderer(_editAreaSymbol),
                }
            );
            MyMapView.GraphicsOverlays.Add(
                new GraphicsOverlay()
                {
                    Id = STARTING_POINTS,
                    Renderer = new SimpleRenderer(_startingPointsSymbol),
                }
            );

            _popupItemsSource.CollectionChanged += (s, e) =>
            {
                PopupGrid.IsVisible = _popupItemsSource.Count > 0;
            };

            UseLocalCache.IsVisible = Directory.Exists(_localCachePath);

            // Determines whether app have internet access
            _isOnline = Connectivity.Current.ConnectionProfiles.Contains(ConnectionProfile.WiFi);
            Connectivity.ConnectivityChanged += (s, e) =>
            {
                _isOnline = e.NetworkAccess == NetworkAccess.Internet;
                if (!_isOnline)
                {
                    ResetConfigurationWhenOffline();
                }
            };
            _ = InitializeAsync();
            #endregion INITIALIZATION
        }

        #region HELPER METHODS

        #region LOAD MAP

        // Changes the visibility of map and configuration panel to display the branch version name
        void IRecipient<DisplayBranchMessage>.Receive(DisplayBranchMessage message)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (message.Value)
                {
                    _previousStatus = Status.Text;
                    Status.Text = _branchVersionName;
                }
                else
                {
                    Status.Text = _previousStatus;
                }
                ConfigureGrid.IsVisible = !ConfigureGrid.IsVisible;
                MapGrid.IsVisible = !MapGrid.IsVisible;
            });
        }

        // Depending on connectivity and availability of a local cache, retrieves map from web or mobile map
        private async Task<Map> GetMapAsync(
            bool useLocalCache,
            bool takeWebmapOffline,
            bool downloadMapArea,
            bool loadWebmap
        )
        {
            Map? map = null;
            if (loadWebmap && _isOnline)
            {
                Status.Text = "Load web map...";
                ArgumentNullException.ThrowIfNull(_onlineMap);
                map = _onlineMap;
            }
            else if (useLocalCache || !_isOnline)
            {
                try
                {
                    Status.Text = "Use local cache...";
                    var mmpk = await MobileMapPackage.OpenAsync(_localCachePath);
                    if (mmpk.Maps.FirstOrDefault() is Map cachedMap)
                    {
                        if (cachedMap.UtilityNetworks.FirstOrDefault() is UtilityNetwork un)
                        {
                            await un.LoadAsync();
                            if (un.DirtyAreaTable is not null && un.DirtyAreaTable.Layer is null)
                            {
                                cachedMap.OperationalLayers.Add(
                                    new FeatureLayer(un.DirtyAreaTable)
                                );
                            }
                        }
                        _offlineMap = cachedMap;
                        map = _offlineMap;
                    }
                }
                catch { }
            }
            else if (_isOnline)
            {
                ArgumentNullException.ThrowIfNull(_offlineTask);

                if (
                    _offlineMap?.UtilityNetworks.FirstOrDefault() is UtilityNetwork offlineUN
                    && offlineUN.Geodatabase is Geodatabase gdb
                )
                {
                    gdb.Close();
                }

                if (Directory.Exists(_localCachePath))
                    Directory.Delete(_localCachePath, true);

                var areaOfInterest = _offlineTask.PortalItem?.Extent;
                if (_offlineTask.OnlineMap is Map onlineMap)
                {
                    _cts?.Token.ThrowIfCancellationRequested();
                    await onlineMap.LoadAsync();
                    if (
                        onlineMap.UtilityNetworks.FirstOrDefault() is UtilityNetwork onlineUN
                        && _isOnline
                    )
                    {
                        _cts?.Token.ThrowIfCancellationRequested();
                        await onlineUN.LoadAsync();
                        areaOfInterest = onlineUN.Definition?.Extent;
                    }
                }

                if (takeWebmapOffline)
                {
                    ArgumentNullException.ThrowIfNull(areaOfInterest);
                    Status.Text = "Taking map offline...";

                    _cts?.Token.ThrowIfCancellationRequested();
                    var parameters =
                        await _offlineTask.CreateDefaultGenerateOfflineMapParametersAsync(
                            areaOfInterest
                        );

                    var job = _offlineTask.GenerateOfflineMap(parameters, _localCachePath);
                    _cts?.Token.Register(async () => await job.CancelAsync());
                    job.MessageAdded += (s, e) =>
                    {
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            Status.Text = $"{e.Severity}: {e.Message}";
                        });
                    };

                    _cts?.Token.ThrowIfCancellationRequested();
                    var result = await job.GetResultAsync();

                    map = result.OfflineMap;
                    _offlineMap = map;
                }
                else if (downloadMapArea)
                {
                    var mapArea = MapAreaPicker.SelectedItem as PreplannedMapArea;
                    ArgumentNullException.ThrowIfNull(mapArea);

                    _cts?.Token.ThrowIfCancellationRequested();
                    var downloadParameters =
                        await _offlineTask.CreateDefaultDownloadPreplannedOfflineMapParametersAsync(
                            mapArea
                        );

                    var job = _offlineTask.DownloadPreplannedOfflineMap(
                        downloadParameters,
                        _localCachePath
                    );
                    _cts?.Token.Register(async () => await job.CancelAsync());
                    job.MessageAdded += (s, e) =>
                    {
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            Status.Text = $"{e.Severity}: {e.Message}";
                        });
                    };
                    _cts?.Token.ThrowIfCancellationRequested();
                    var result = await job.GetResultAsync();

                    map = result.OfflineMap;
                    _offlineMap = map;
                }
            }

            ArgumentNullException.ThrowIfNull(map);
            await map.LoadAsync();

            var utilityNetwork = map.UtilityNetworks.FirstOrDefault();
            ArgumentNullException.ThrowIfNull(utilityNetwork);
            await utilityNetwork.LoadAsync();

            if (_isOnline)
            {
                if (utilityNetwork.Geodatabase is Geodatabase gdb)
                    _branchVersionName = await gdb.GetReplicaVersionAsync() ?? string.Empty;
            }

            UseLocalCache.IsVisible = Directory.Exists(_localCachePath);
            return map;
        }

        // Clears graphics and feature selection on the map while determining if edits exists
        private void Reset(bool skipStarting = false)
        {
            MyMapView.DismissCallout();
            _current = null;
            _popupItemsSource.Clear();
            if (MyMapView.Map is Map map)
            {
                foreach (var layer in map.OperationalLayers.ToFeatureLayers())
                    layer.ClearSelection();
            }
            if (MyMapView.GraphicsOverlays != null)
            {
                foreach (var overlay in MyMapView.GraphicsOverlays)
                {
                    if (skipStarting && overlay.Id == STARTING_POINTS)
                    {
                        continue;
                    }
                    overlay.Graphics.Clear();
                }
            }
            BusyIndicator.IsVisible = false;
            PopupGrid.IsVisible = false;
            Status.Text = string.Empty;
            _previousStatus = string.Empty;
            SendButton.IsVisible = false;
            MarkInoperableGrid.IsVisible = false;
            if (
                MyMapView.Map?.UtilityNetworks.FirstOrDefault() is UtilityNetwork utilityNetwork
                && utilityNetwork.Geodatabase is Geodatabase gdb
            )
            {
                var hasEdits = gdb.HasLocalEdits();
                UndoEdits.IsEnabled = hasEdits;
                SyncEdits.IsEnabled = hasEdits;
            }
            else
            {
                UndoEdits.IsEnabled = false;
                SyncEdits.IsEnabled = false;
            }
            CancelSync.IsVisible = false;
        }

        // Resets the options available when offline
        private void ResetConfigurationWhenOffline()
        {
            LoadWebmap.IsChecked = false;
            LoadWebmap.IsVisible = false;
            TakeWebmapOffline.IsChecked = false;
            TakeWebmapOffline.IsVisible = false;
            DownloadMapArea.IsChecked = false;
            DownloadMapArea.IsVisible = false;
        }

        // Displays available web maps on the portal
        private async Task InitializeAsync()
        {
            try
            {
                if (!_isOnline)
                {
                    ResetConfigurationWhenOffline();
                    return;
                }

                BusyIndicator.IsVisible = true;
                Status.Text = "Loading web maps...";

                var portal = await ArcGISPortal.CreateAsync(
                    new Uri(MauiProgram.PORTAL_ACCESS.Url),
                    true
                );

                var query = new PortalQueryParameters(
                    "(owner:admin) type:(+\"Web Map\" -\"Web Mapping Application\")"
                );
                query.Limit = 100;
                var webmap_items = await portal.FindItemsAsync(query);
                foreach (var webmap in webmap_items.Results)
                {
                    if (webmap.Type == PortalItemType.WebMap)
                        _webmapItems.Add(webmap);
                }
                if (_webmapItems.Count > 0)
                    WebmapPicker.SelectedIndex = 0;
                Status.Text = "Web maps loaded.";
            }
            catch (Exception ex)
            {
                Status.Text = $"Loading web maps failed {ex.GetType().Name}:{ex.Message}";
            }
            finally
            {
                BusyIndicator.IsVisible = false;
            }
        }

        // Displays available map areas for download for the selected web map
        private async void OnWebmapChanged(object sender, EventArgs e)
        {
            if (
                sender is not Picker picker
                || picker.SelectedItem is not PortalItem item
                || item.Type != PortalItemType.WebMap
                || !_isOnline
            )
                return;
            try
            {
                BusyIndicator.IsVisible = true;
                Status.Text = "Loading map areas...";

                _onlineMap = new Map(item);
                LoadWebmap.IsVisible = _onlineMap is not null;
                _offlineTask = await OfflineMapTask.CreateAsync(item);
                var areas = await _offlineTask.GetPreplannedMapAreasAsync();
                _mapAreaItems.Clear();
                foreach (var area in areas)
                {
                    try
                    {
                        await area.LoadAsync();
                        _mapAreaItems.Add(area);
                    }
                    catch { }
                }

                if (_mapAreaItems.Count > 0)
                    MapAreaPicker.SelectedIndex = 0;
                else
                    MapAreaPicker.SelectedIndex = -1;
                DownloadMapArea.IsVisible = _mapAreaItems.Count > 0;
                Status.Text = "Map areas loaded.";
            }
            catch (Exception ex)
            {
                Status.Text = $"Loading map areas failed {ex.GetType().Name}:{ex.Message}";
            }
            finally
            {
                BusyIndicator.IsVisible = false;
            }
        }

        // For canceling jobs: generate/download offline map and sync.
        private void OnCancelLoad(object sender, EventArgs e)
        {
            _cts?.Cancel();
        }

        // Load map based on selected configuration
        private async void OnLoadMapClicked(object? sender, EventArgs e)
        {
            try
            {
                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                BusyIndicator.IsVisible = true;

                Status.Text = "Loading map...";

                MyMapView.Map = await GetMapAsync(
                    UseLocalCache.IsChecked,
                    TakeWebmapOffline.IsChecked,
                    DownloadMapArea.IsChecked,
                    LoadWebmap.IsChecked
                );

                Status.Text = "Map loaded.";
                ConfigureGrid.IsVisible = false;
                MapGrid.IsVisible = true;
            }
            catch (Exception ex)
            {
                await DisplayAlert(ex.GetType().Name, ex.Message, "OK");
            }
            finally
            {
                BusyIndicator.IsVisible = false;
            }
        }

        #endregion LOAD MAP

        #region LOCATE ASSET

        // Locates the pipe for replacement
        private async Task LocateAssetAsync(string assetId)
        {
            if (
                MyMapView.Map is not Map map
                || map.UtilityNetworks.FirstOrDefault() is not UtilityNetwork utilityNetwork
                || MyMapView.GraphicsOverlays?.FirstOrDefault(o => o.Id == STARTING_POINTS)
                    is not GraphicsOverlay graphicsOverlay
                || utilityNetwork.Definition?.NetworkSources?.FirstOrDefault(n =>
                    n.SourceUsageType == UtilityNetworkSourceUsageType.Line
                )
                    is not UtilityNetworkSource networkSource
                || networkSource.FeatureTable is not ArcGISFeatureTable table
            )
                return;

            try
            {
                Reset();

                BusyIndicator.IsVisible = true;
                assetId = assetId.StartsWith("Dstrbtn-Pp-") ? assetId : $"Dstrbtn-Pp-{assetId}";
                Status.Text = $"Locating {assetId}...";

                var query = new QueryParameters()
                {
                    WhereClause = $"{ASSETID_FIELD_NAME} = '{assetId}'",
                };
                var result = await table.QueryFeaturesAsync(query);
                var feature = result.FirstOrDefault() as ArcGISFeature;

                ArgumentNullException.ThrowIfNull(feature);
                var flayer = table.Layer as FeatureLayer;
                ArgumentNullException.ThrowIfNull(flayer);

                flayer.SelectFeature(feature);
                await feature.LoadAsync();

                if (
                    feature.Geometry is Geometry geometry
                    && GeometryEngine.Buffer(geometry, LOCATE_BUFFER_DISTANCE)
                        is Geometry bufferedGeometry
                    && bufferedGeometry.Extent is Envelope extent
                    && Popup.FromGeoElement(feature) is Popup popup
                    && feature.Geometry is Polyline lineGeometry
                    && lineGeometry.CreatePointAlong(lineGeometry.Length() / 2) is MapPoint mapPoint
                )
                {
                    graphicsOverlay.Graphics.Add(new Graphic(mapPoint));
                    _elementForPipelineToReplace = utilityNetwork.CreateElement(feature);
                    MyMapView.ShowCalloutForGeoElement(
                        popup.GeoElement,
                        MyMapView.LocationToScreen(mapPoint),
                        new CalloutDefinition(
                            $"{_elementForPipelineToReplace.AssetGroup.Name}",
                            $"{_elementForPipelineToReplace.AssetType.Name} ({assetId})"
                        )
                    );
                    _ = MyMapView.SetViewpointGeometryAsync(extent, VIEWPOINT_PADDING);
                }
                Status.Text = $"{assetId} found...";
            }
            catch (Exception ex)
            {
                Status.Text = $"Locating asset failed {ex.GetType().Name}:{ex.Message}";
            }
            finally
            {
                BusyIndicator.IsVisible = false;
            }
        }

        // Locates the pipe from search bar
        private async void OnSearchButtonPressed(object sender, EventArgs e)
        {
            if (sender is SearchBar searchBar)
            {
                searchBar.Text = "1710";

                var assetId = searchBar.Text;
                await LocateAssetAsync(assetId);
            }
        }

        // Locates the pipe from tapped location
        private async void OnGeoViewTapped(object sender, GeoViewInputEventArgs e)
        {
            if (
                MyMapView.Map is null
                || MyMapView.Map.UtilityNetworks.FirstOrDefault()
                    is not UtilityNetwork utilityNetwork
                || utilityNetwork.Definition?.NetworkSources?.FirstOrDefault(n =>
                    n.SourceUsageType == UtilityNetworkSourceUsageType.Line
                )
                    is not UtilityNetworkSource networkSource
                || networkSource.FeatureTable is not ArcGISFeatureTable table
                || table.Layer is not FeatureLayer layer
            )
                return;

            try
            {
                BusyIndicator.IsVisible = true;

                var result = await MyMapView.IdentifyLayerAsync(layer, e.Position, 10, false);
                var feature =
                    result.GeoElements.FirstOrDefault() as ArcGISFeature
                    ?? result
                        .SublayerResults.FirstOrDefault(r => r.GeoElements.Any())
                        ?.GeoElements.FirstOrDefault() as ArcGISFeature;
                if (feature?.GetAttributeValue(ASSETID_FIELD_NAME)?.ToString() is string assetId)
                    await LocateAssetAsync(assetId);
            }
            catch (Exception ex)
            {
                Status.Text = $"Identifying new asset failed {ex.GetType().Name}:{ex.Message}";
            }
            finally
            {
                BusyIndicator.IsVisible = false;
            }
        }
        #endregion LOCATE ASSET

        #region TRACE

        // Selects and zooms the map on features returned by trace
        private async Task<
            IEnumerable<KeyValuePair<FeatureLayer, IEnumerable<Feature>>>
        > ProcessTraceResultsAsync(
            UtilityElementTraceResult elementResult,
            bool showPopup,
            bool autoZoom
        )
        {
            var result = new Dictionary<FeatureLayer, IEnumerable<Feature>>();
            if (
                MyMapView.Map is not Map map
                || map.UtilityNetworks.FirstOrDefault() is not UtilityNetwork utilityNetwork
            )
                return result;

            var resultExtents = new List<Envelope>();
            foreach (var featureLayer in map.OperationalLayers.ToFeatureLayers())
            {
                var elements = elementResult.Elements.Where(element =>
                    element.NetworkSource.FeatureTable == featureLayer.FeatureTable
                );
                if (!elements.Any())
                    continue;

                var features = await utilityNetwork.GetFeaturesForElementsAsync(elements);
                if (showPopup && !autoZoom)
                {
                    result[featureLayer] = features;
                }
                else
                {
                    featureLayer.SelectFeatures(features);
                }

                if (showPopup)
                {
                    foreach (var f in features)
                    {
                        _popupItemsSource.Add(Popup.FromGeoElement(f));
                    }
                }
                if (autoZoom)
                {
                    resultExtents.Add(
                        GeometryEngine.CombineExtents(
                            (IEnumerable<Geometry>)
                                features
                                    .Select(m => m.Geometry?.Buffer(BUFFER_DISTANCE))
                                    .OfType<Geometry>()
                                    .ToList()
                        )
                    );
                }
            }
            if (
                autoZoom
                && resultExtents.Count > 0
                && GeometryEngine.CombineExtents(resultExtents) is Envelope resultExtent
                && GeometryEngine.Buffer(resultExtent, LOCATE_BUFFER_DISTANCE)
                    is Geometry bufferedGeometry
                && bufferedGeometry.Extent is Envelope extent
            )
            {
                await MyMapView.SetViewpointGeometryAsync(resultExtent, VIEWPOINT_PADDING);
            }
            return result;
        }

        // For displaying feature attributes
        private Dictionary<string, string> GetItemsSource(
            ArcGISFeature feature,
            UtilityElement element
        )
        {
            var itemsSource = new Dictionary<string, string>();
            var table = feature.FeatureTable!;
            foreach (var item in feature.Attributes)
            {
                if (!_fieldNames.Contains(item.Key))
                    continue;

                if (table.GetField(item.Key) is Field field)
                {
                    var value = item.Value;
                    if (field.Domain is CodedValueDomain cvd)
                    {
                        value = cvd
                            .CodedValues.FirstOrDefault(c => c.Code?.Equals(item.Value) == true)
                            ?.Name;
                    }
                    else if (field.Name == "assetgroup")
                    {
                        value = element.AssetGroup.Name;
                    }
                    else if (field.Name == "assettype")
                    {
                        value = element.AssetType.Name;
                    }
                    if (
                        element.AssetGroup.Name == "Meter"
                        && element.AssetType.Name == "Customer"
                        && (field.Name == "turnstoclose" || field.Name == "operable")
                    )
                        continue;
                    itemsSource[field.Alias] = $"{value}";
                }
            }
            return itemsSource;
        }

        // For displaying attributes while navigating features backward
        private void OnPrevious(object sender, EventArgs e)
        {
            if (_current != null)
            {
                var indexOf = _popupItemsSource.IndexOf(_current);
                _current = _popupItemsSource.ElementAtOrDefault(indexOf - 1);
                OnCurrentItemChanged();
            }
        }

        // For displaying attributes while navigating features forward
        private void OnNext(object sender, EventArgs e)
        {
            if (_current != null)
            {
                var indexOf = _popupItemsSource.IndexOf(_current);
                _current = _popupItemsSource.ElementAtOrDefault(indexOf + 1);
                OnCurrentItemChanged();
            }
        }

        // Displays attributes and popup of the current feature selection
        private void OnCurrentItemChanged()
        {
            try
            {
                MyMapView.DismissCallout();
                if (_current is null)
                {
                    Previous.IsEnabled = false;
                    Next.IsEnabled = false;
                }
                else if (
                    _current is Popup popup
                    && popup.GeoElement is ArcGISFeature feature
                    && feature.Geometry is MapPoint mapPoint
                    && MyMapView.Map?.UtilityNetworks.FirstOrDefault()
                        is UtilityNetwork utilityNetwork
                    && feature.GetAttributeValue(ASSETID_FIELD_NAME) is string assetId
                )
                {
                    BusyIndicator.IsVisible = false;
                    var element = utilityNetwork.CreateElement(feature);
                    MyMapView.ShowCalloutForGeoElement(
                        popup.GeoElement,
                        MyMapView.LocationToScreen(mapPoint),
                        new CalloutDefinition(
                            $"{element.AssetGroup.Name}",
                            $"{element.AssetType.Name} ({assetId})"
                        )
                    );
                    if (
                        utilityNetwork.Definition is UtilityNetworkDefinition definition
                        && feature.FeatureTable is ArcGISFeatureTable table
                        && definition.NetworkSources.FirstOrDefault(n => n.FeatureTable == table)
                            is UtilityNetworkSource
                    )
                    {
                        MyPopupViewer.ItemsSource = GetItemsSource(feature, element);
                    }

                    var indexOf = _popupItemsSource.IndexOf(_current);
                    Previous.IsEnabled =
                        _popupItemsSource.ElementAtOrDefault(indexOf - 1) is not null;
                    Next.IsEnabled = _popupItemsSource.ElementAtOrDefault(indexOf + 1) is not null;
                }
            }
            catch { }
        }

        // Runs the trace based on selected named trace configuration
        private async Task RunTraceAsync(string action)
        {
            if (
                MyMapView.Map is not Map map
                || map.UtilityNetworks.FirstOrDefault() is not UtilityNetwork utilityNetwork
                || _elementForPipelineToReplace is not UtilityElement startingPoint
            )
                return;
            try
            {
                bool isIsolatingValves = action == "Isolating valves";
                var findNamedTrace = isIsolatingValves
                    ? NAMED_TRACE_ISOLATING_FEATURES_ONLY
                    : NAMED_TRACE_ALL_ISOLATED_FEATURES;

                Reset(true);
                BusyIndicator.IsVisible = true;
                Status.Text = $"Running an isolation trace that find {action}...";
                await utilityNetwork.LoadAsync();

                var namedTraces = await map.GetNamedTraceConfigurationsFromUtilityNetworkAsync(
                    utilityNetwork
                );

                var namedTrace = namedTraces.FirstOrDefault(n => n.Name == findNamedTrace);
                ArgumentNullException.ThrowIfNull(namedTrace);
                var traceSummary = string.Empty;

                var parameters = new UtilityTraceParameters(namedTrace, [startingPoint]);
                var result = await utilityNetwork.TraceAsync(parameters);
                var elementResult =
                    result.FirstOrDefault(r => r is UtilityElementTraceResult)
                    as UtilityElementTraceResult;
                ArgumentNullException.ThrowIfNull(elementResult);
                var showPopup = findNamedTrace == NAMED_TRACE_ALL_ISOLATED_FEATURES ? false : true;

                if (findNamedTrace == NAMED_TRACE_ISOLATING_FEATURES_ONLY)
                {
                    traceSummary = $"{elementResult.Elements.Count} valves found.";
                    Status.Text = traceSummary;
                }

                await ProcessTraceResultsAsync(elementResult, showPopup, true);
                _current = _popupItemsSource.FirstOrDefault();
                OnCurrentItemChanged();
                if (isIsolatingValves)
                {
                    MarkInoperableGrid.IsVisible = true;
                    SendButton.IsVisible = false;
                }
                else
                {
                    MarkInoperableGrid.IsVisible = false;
                    SendButton.IsVisible = true;
                }
                await DisplayAlert(traceSummary, "", "OK");
            }
            catch (Exception ex)
            {
                Status.Text = $"Trace failed {ex.GetType().Name}:{ex.Message}";
            }
            finally
            {
                BusyIndicator.IsVisible = false;
            }
        }

        // Locates the isolating valves to close for the pipe's safe repair
        private async void OnRunTraceIsolatingValves(object sender, EventArgs e)
        {
            await RunTraceAsync("Isolating valves");
        }

        // Determines customer impact of the repair
        private async void OnRunTraceCustomerMeters(object sender, EventArgs e)
        {
            await RunTraceAsync("Customer meters");
        }

        #endregion

        // Displays attribute of the feature
        private void UpdateAttributeViewer()
        {
            if (
                MyMapView.Map is not Map map
                || map.UtilityNetworks.FirstOrDefault() is not UtilityNetwork utilityNetwork
                || _current is not Popup popup
                || popup.GeoElement is not ArcGISFeature feature
            )
                return;
            MyPopupViewer.ItemsSource = GetItemsSource(
                feature,
                utilityNetwork.CreateElement(feature)
            );
        }

        // Validates the dirty area using the updated feature's geometry
        private async Task ValidateAsync()
        {
            if (
                MyMapView.Map is not Map map
                || map.UtilityNetworks.FirstOrDefault() is not UtilityNetwork utilityNetwork
                || _current is not Popup popup
                || popup.GeoElement is not ArcGISFeature feature
                || MyMapView.GraphicsOverlays?.FirstOrDefault(o => o.Id == EDIT_AREA)
                    is not GraphicsOverlay graphicsOverlay
                || utilityNetwork.Geodatabase is not Geodatabase gdb
            )
                return;
            if (
                feature.Geometry is not MapPoint center
                || GeometryEngine.Buffer(center, BUFFER_DISTANCE) is not Geometry geometry
                || geometry.Extent is not Envelope extent
            )
            {
                BusyIndicator.IsVisible = false;
                return;
            }

            var newGraphic = new Graphic(extent);
            graphicsOverlay.Graphics.Add(newGraphic);

            await MyMapView.SetViewpointGeometryAsync(extent, VIEWPOINT_PADDING);
            // For visualization purpose only to show dirty area gets created and resolved
            // await Task.Delay(1000);

            ArgumentNullException.ThrowIfNull(extent);
            var job = utilityNetwork.ValidateNetworkTopology(extent);
            await job.GetResultAsync();

            UndoEdits.IsEnabled = true;
            SyncEdits.IsEnabled = true;

            graphicsOverlay.Graphics.Clear();
        }

        // Undo edits
        private void OnUndoEdits(object sender, EventArgs e)
        {
            try
            {
                if (
                    MyMapView.Map?.UtilityNetworks.FirstOrDefault() is UtilityNetwork utilityNetwork
                )
                {
                    if (utilityNetwork.Geodatabase is Geodatabase gdb && gdb.IsInTransaction)
                    {
                        Status.Text = $"Rolling back transaction...";
                        gdb.RollbackTransaction();
                    }

                    foreach (var item in _popupItemsSource)
                    {
                        if (item.GeoElement is ArcGISFeature feature)
                        {
                            feature.Refresh();
                            MyPopupViewer.ItemsSource = GetItemsSource(
                                feature,
                                utilityNetwork.CreateElement(feature)
                            );
                        }
                    }
                }
                UndoEdits.IsEnabled = false;
                SyncEdits.IsEnabled = false;
                CancelSync.IsVisible = false;
            }
            catch (Exception ex)
            {
                Status.Text = $"Rolling back edits failed {ex.GetType().Name}:{ex.Message}";
            }
            finally
            {
                BusyIndicator.IsVisible = false;
            }
        }

        #endregion HELPER METHODS

        // Marks valve inoperable with reason
        private async void OnInoperable(object sender, EventArgs e)
        {
            #region CHECK CONDITIONS
            if (
                MyMapView.Map is not Map map
                || map.UtilityNetworks.FirstOrDefault() is not UtilityNetwork utilityNetwork
                || _current is not Popup popup
                || popup.GeoElement is not ArcGISFeature feature
                || feature.FeatureTable is not ArcGISFeatureTable table
                || utilityNetwork.Definition is not UtilityNetworkDefinition definition
                || definition.NetworkSources.FirstOrDefault(n => n.FeatureTable == table)
                    is not UtilityNetworkSource networkSource
                || networkSource.SourceUsageType != UtilityNetworkSourceUsageType.Device
                || table.GetField(OPERABLE_FIELD_NAME) is not Field operableField
                || table.GetField(NOTES_FIELD_NAME) is not Field inoperableReasonField
                || operableField.Domain is not CodedValueDomain cvd
                || cvd.CodedValues.FirstOrDefault(c =>
                    c.Name.Equals(NON_OPERABLE_CODE_NAME, StringComparison.OrdinalIgnoreCase)
                )
                    is not CodedValue inoperableCodedValue
                || cvd.CodedValues.FirstOrDefault(c =>
                    c.Name.Equals(OPERABLE_CODE_NAME, StringComparison.OrdinalIgnoreCase)
                )
                    is not CodedValue operableCodedValue
                || MyMapView.GraphicsOverlays?.FirstOrDefault(o => o.Id == EDIT_AREA)
                    is not GraphicsOverlay graphicsOverlay
                || utilityNetwork.Geodatabase is not Geodatabase gdb
            )
                return;
            #endregion CHECK CONDITIONS

            try
            {
                if (InoperableReasons.SelectedItem is null)
                    InoperableReasons.SelectedIndex = 0;
                var inoperableReason = InoperableReasons.SelectedItem as string;
                BusyIndicator.IsVisible = true;
                Status.Text = $"Marking valve inoperable...";

                if (!gdb.IsInTransaction)
                    gdb.BeginTransaction();

                await feature.LoadAsync();

                feature.SetAttributeValue(operableField.Name, inoperableCodedValue.Code);
                feature.SetAttributeValue(inoperableReasonField.Name, inoperableReason);

                await table.UpdateFeatureAsync(feature);

                UpdateAttributeViewer();

                await ValidateAsync();
                Status.Text = $"Valve marked inoperable...";
            }
            catch (Exception ex)
            {
                Status.Text = $"Marking valve inoperable failed {ex.GetType().Name}:{ex.Message}";
            }
            finally
            {
                BusyIndicator.IsVisible = false;
            }
        }

        // Synchronizes the edit back to replica version
        private async void OnSync(object sender, EventArgs e)
        {
            #region CHECK CONDITIONS
            if (
                MyMapView.Map is not Map map
                || map.Item is not LocalItem item
                || item.Type != LocalItemType.MobileMap
                || map.UtilityNetworks.FirstOrDefault() is not UtilityNetwork utilityNetwork
                || utilityNetwork.Geodatabase is not Geodatabase gdb
                || !_isOnline
            )
                return;
            #endregion CHECK CONDITIONS

            try
            {
                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                CancelSync.IsVisible = true;
                BusyIndicator.IsVisible = true;

                if (gdb.IsInTransaction)
                    gdb.CommitTransaction();

                _branchVersionName = await gdb.GetReplicaVersionAsync() ?? string.Empty;

                var task = await OfflineMapSyncTask.CreateAsync(map);

                var parameters = await task.CreateDefaultOfflineMapSyncParametersAsync();

                parameters.ReconcileBranchVersion = true;

                var job = task.SyncOfflineMap(parameters);
                _cts?.Token.Register(async () => await job.CancelAsync());

                job.MessageAdded += (s, e) =>
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        Status.Text = $"{e.Severity}: {e.Message}";
                    });
                };

                var result = await job.GetResultAsync();
            }
            catch (Exception ex)
            {
                Status.Text = $"Sync edits failed {ex.GetType().Name}:{ex.Message}";
            }
            finally
            {
                CancelSync.IsVisible = false;
                BusyIndicator.IsVisible = false;
            }
        }
    }
}
