using System.Text;
using FellowOakDicom;
using FellowOakDicom.Imaging.Codec;
using FellowOakDicom.Network;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MedMissionBridge.Dicom;

public static class DicomSetup
{
    private static bool _done;
    private static readonly object Gate = new();

    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_done) return;
            new DicomSetupBuilder()
                .RegisterServices(s => s.AddFellowOakDicom())
                .Build();
            _done = true;
        }
    }
}

public class MwlService(INetworkStream stream, Encoding fallbackEncoding, ILogger log,
        DicomServiceDependencies dependencies)
    : DicomService(stream, fallbackEncoding, log, dependencies),
      IDicomServiceProvider, IDicomCEchoProvider, IDicomCFindProvider
{
    /// <summary>Set by the host before the server starts; tests inject fakes.</summary>
    public static Func<Task<IReadOnlyList<DicomDataset>>>? WorklistSource { get; set; }

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        foreach (var pc in association.PresentationContexts)
        {
            if (pc.AbstractSyntax == DicomUID.Verification
                || pc.AbstractSyntax == DicomUID.ModalityWorklistInformationModelFind)
                pc.AcceptTransferSyntaxes(
                    DicomTransferSyntax.ExplicitVRLittleEndian,
                    DicomTransferSyntax.ImplicitVRLittleEndian);
            else
                pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
        }
        return SendAssociationAcceptAsync(association);
    }

    public Task OnReceiveAssociationReleaseRequestAsync() => SendAssociationReleaseResponseAsync();
    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason) { }
    public void OnConnectionClosed(Exception? exception) { }

    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request) =>
        Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));

    public async IAsyncEnumerable<DicomCFindResponse> OnCFindRequestAsync(DicomCFindRequest request)
    {
        IReadOnlyList<DicomDataset>? items = null;
        var source = WorklistSource;
        if (source is not null)
        {
            try { items = await source(); }
            catch { items = null; }
        }
        if (items is null)
        {
            yield return new DicomCFindResponse(request, DicomStatus.ProcessingFailure);
            yield break;
        }

        var query = request.Dataset ?? new DicomDataset();
        foreach (var item in items)
            if (WorklistMatcher.Matches(query, item))
                yield return new DicomCFindResponse(request, DicomStatus.Pending) { Dataset = item };

        yield return new DicomCFindResponse(request, DicomStatus.Success);
    }
}
