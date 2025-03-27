using System.Runtime.Serialization;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Http;
using Newtonsoft.Json;

namespace GasUtilityEditor;

internal static class GeodatabaseExtensions
{
    // Determines the branch version name for the replica
    internal static async Task<string> GetReplicaVersionAsync(this Geodatabase geodatabase)
    {
        if (!geodatabase.IsSyncEnabled())
        {
            return $"'{{{geodatabase.SyncId}}}'";
        }
        using var client = new HttpClient(new ArcGISHttpMessageHandler());

        using var result = await client.GetAsync(
            new Uri($"{geodatabase.Source?.OriginalString}/replicas/{geodatabase.SyncId}?f=json")
        );
        var contentString = await result.Content.ReadAsStringAsync();
        var replica = JsonConvert.DeserializeObject<Replica>(contentString);

        return replica?.Version ?? $"'{{{geodatabase.SyncId}}}'";
    }
}

[DataContract]
internal class Replica
{
    [DataMember(Name = "replicaName")]
    public string? Name { get; set; }

    [DataMember(Name = "replicaID")]
    public string? Id { get; set; }

    [DataMember(Name = "replicaVersion")]
    public string? Version { get; set; }
}
