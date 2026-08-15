namespace MedMissionBridge;

public class BridgeOptions
{
    public string ApiKey { get; set; } = "changeme-dev-key";
    public int HttpPort { get; set; } = 18080;
    /// <summary>Empty = default %ProgramData%\MedMissionBridge\bridge.db.</summary>
    public string DbPath { get; set; } = "";
    public MwlOptions Mwl { get; set; } = new();
    public MdnsOptions Mdns { get; set; } = new();

    public string ResolveDataDir()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "MedMissionBridge");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string ResolveDbPath() =>
        string.IsNullOrWhiteSpace(DbPath) ? Path.Combine(ResolveDataDir(), "bridge.db") : DbPath;
}

public class MwlOptions
{
    public int Port { get; set; } = 11112;
    /// <summary>0.0.0.0 = LAN-reachable (default). Set to 127.0.0.1 to keep the
    /// DICOM port off the LAN when only co-located software queries MWL.</summary>
    public string ListenAddress { get; set; } = "0.0.0.0";
    public string AeTitle { get; set; } = "MEDMISSION";
    public string Modality { get; set; } = "CR";
    public string StationAeTitle { get; set; } = "MEDMISSION";
    public string ProcedureDescription { get; set; } = "TB Screening Chest X-Ray";
}

public class MdnsOptions
{
    /// <summary>Empty = machine name.</summary>
    public string ServiceName { get; set; } = "";
    public string ResolveServiceName() =>
        string.IsNullOrWhiteSpace(ServiceName) ? Environment.MachineName : ServiceName;
}
