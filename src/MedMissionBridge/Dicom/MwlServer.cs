using FellowOakDicom.Network;

namespace MedMissionBridge.Dicom;

public sealed class MwlServer : IDisposable
{
    private readonly IDicomServer _server;
    public MwlServer(int port) => _server = DicomServerFactory.Create<MwlService>(port);
    public void Dispose() => _server.Dispose();
}
