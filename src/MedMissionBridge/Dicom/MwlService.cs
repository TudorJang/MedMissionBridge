using System.Collections.Concurrent;
using System.Text;
using FellowOakDicom;
using MedMissionBridge.Data;
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
            // Without this the DICOM services log into a container of their own and the
            // log file never sees a C-FIND or an MPPS message — which is exactly what
            // an operator is told to check when the worklist misbehaves.
            new DicomSetupBuilder()
                .RegisterServices(s => s
                    .AddFellowOakDicom()
                    .AddLogging(builder => Serilog.SerilogLoggingBuilderExtensions
                        .AddSerilog(builder, dispose: false)))
                .Build();
            _done = true;
        }
    }
}

public class MwlService(INetworkStream stream, Encoding fallbackEncoding, ILogger log,
        DicomServiceDependencies dependencies)
    : DicomService(stream, fallbackEncoding, log, dependencies),
      IDicomServiceProvider, IDicomCEchoProvider, IDicomCFindProvider, IDicomNServiceProvider
{
    /// <summary>Set by the host before the server starts; tests inject fakes.</summary>
    public static Func<Task<IReadOnlyList<DicomDataset>>>? WorklistSource { get; set; }

    /// <summary>Applies a status the console reported and answers whether the survey
    /// exists at all. Set by the host; tests inject fakes.</summary>
    public static Func<string, WorklistStatus, Task<bool>>? StatusSink { get; set; }

    /// <summary>
    /// Which survey each performed-procedure-step instance belongs to. The N-SET that
    /// closes a step carries only the step's own SOP Instance UID, so the link made at
    /// N-CREATE has to survive until then. It lives in memory: a bridge restarted
    /// mid-exposure loses one link, and the operator closes that study by hand.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> StepRecords = new();

    /// <summary>Tests reset the shared map so one case cannot leak into the next.</summary>
    public static void ForgetSteps() => StepRecords.Clear();

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        // The AE title (Mwl:AeTitle) is advertised only; it is not enforced here.
        // Log who connected so an operator can see it in the log, but accept any
        // calling/called AE — see README.
        Serilog.Log.Information(
            "MWL association request: CalledAE={CalledAe}, CallingAE={CallingAe}, RemoteHost={RemoteHost}",
            association.CalledAE, association.CallingAE, association.RemoteHost);

        foreach (var pc in association.PresentationContexts)
        {
            if (pc.AbstractSyntax == DicomUID.Verification
                || pc.AbstractSyntax == DicomUID.ModalityWorklistInformationModelFind
                || pc.AbstractSyntax == DicomUID.ModalityPerformedProcedureStep)
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
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to load the worklist source for a C-FIND query");
                items = null;
            }
        }
        if (items is null)
        {
            yield return new DicomCFindResponse(request, DicomStatus.ProcessingFailure);
            yield break;
        }

        var query = request.Dataset ?? new DicomDataset();
        var matchCount = 0;
        foreach (var item in items)
            if (WorklistMatcher.Matches(query, item))
            {
                matchCount++;
                yield return new DicomCFindResponse(request, DicomStatus.Pending) { Dataset = item };
            }

        // These are query keys typed by the operator of the modality (not tablet
        // survey data), so logging them is acceptable.
        var queryDate = query.TryGetSequence(DicomTag.ScheduledProcedureStepSequence, out var qSeq) && qSeq.Items.Count > 0
            ? qSeq.Items[0].GetSingleValueOrDefault(DicomTag.ScheduledProcedureStepStartDate, string.Empty)
            : string.Empty;
        Logger.LogInformation(
            "C-FIND query PatientID={PatientId} PatientName={PatientName} Date={Date} matched {MatchCount} item(s)",
            query.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty),
            query.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty),
            queryDate, matchCount);

        yield return new DicomCFindResponse(request, DicomStatus.Success);
    }

    // ---- Modality Performed Procedure Step -------------------------------------------

    public async Task<DicomNCreateResponse> OnNCreateRequestAsync(DicomNCreateRequest request)
    {
        var stepUid = request.SOPInstanceUID?.UID ?? string.Empty;
        var dataset = request.Dataset ?? new DicomDataset();
        var recordId = MppsMapping.FindRecordId(dataset);
        var status = MppsMapping.ToWorklistStatus(
            dataset.GetSingleValueOrDefault(DicomTag.PerformedProcedureStepStatus, string.Empty));

        if (recordId is null)
        {
            // Answering Success would tell the console the step is tracked when it is
            // not, and the study would silently never close.
            Logger.LogWarning("MPPS N-CREATE {StepUid} matched no survey", stepUid);
            return new DicomNCreateResponse(request, DicomStatus.NoSuchObjectInstance);
        }

        Logger.LogInformation("MPPS N-CREATE {StepUid} -> {RecordId} ({Status})",
            stepUid, recordId, status?.ToString() ?? "no status");

        if (!await ApplyAsync(recordId, status))
        {
            // The id is well formed but names no survey here. Success would tell the
            // console the step is tracked when nothing is tracking it.
            Logger.LogWarning("MPPS N-CREATE {StepUid} names {RecordId}, which is not a survey here",
                stepUid, recordId);
            return new DicomNCreateResponse(request, DicomStatus.NoSuchObjectInstance);
        }

        if (stepUid.Length > 0) StepRecords[stepUid] = recordId;
        return new DicomNCreateResponse(request, DicomStatus.Success);
    }

    public async Task<DicomNSetResponse> OnNSetRequestAsync(DicomNSetRequest request)
    {
        var stepUid = request.SOPInstanceUID?.UID ?? string.Empty;
        var dataset = request.Dataset ?? new DicomDataset();
        var status = MppsMapping.ToWorklistStatus(
            dataset.GetSingleValueOrDefault(DicomTag.PerformedProcedureStepStatus, string.Empty));

        // The N-SET normally carries nothing identifying but the step UID, so fall back
        // to the dataset only when the link was lost with a restart.
        if (!StepRecords.TryGetValue(stepUid, out var recordId))
            recordId = MppsMapping.FindRecordId(dataset);

        if (recordId is null)
        {
            Logger.LogWarning(
                "MPPS N-SET {StepUid} matched no survey — the study stays open for the operator to close",
                stepUid);
            return new DicomNSetResponse(request, DicomStatus.NoSuchObjectInstance);
        }

        Logger.LogInformation("MPPS N-SET {StepUid} -> {RecordId} ({Status})",
            stepUid, recordId, status?.ToString() ?? "no status");

        if (!await ApplyAsync(recordId, status))
        {
            Logger.LogWarning("MPPS N-SET {StepUid} names {RecordId}, which is not a survey here",
                stepUid, recordId);
            return new DicomNSetResponse(request, DicomStatus.NoSuchObjectInstance);
        }
        // A finished step will never be referenced again; keeping it would grow forever.
        if (status is WorklistStatus.Completed or WorklistStatus.Cancelled)
            StepRecords.TryRemove(stepUid, out _);

        return new DicomNSetResponse(request, DicomStatus.Success);
    }

    /// <summary>False only when the survey does not exist. A status we do not recognise,
    /// or no sink at all, leaves the record alone without calling the step untracked.</summary>
    private async Task<bool> ApplyAsync(string recordId, WorklistStatus? status)
    {
        if (status is not { } target || StatusSink is not { } sink) return true;
        try { return await sink(recordId, target); }
        catch (Exception ex)
        {
            // A storage failure must not abort the association: the console would
            // retry the whole exposure workflow over a bookkeeping problem.
            Logger.LogError(ex, "Failed to apply {Status} to {RecordId} from MPPS", target, recordId);
            return true;
        }
    }

    public Task<DicomNActionResponse> OnNActionRequestAsync(DicomNActionRequest request) =>
        Task.FromResult(new DicomNActionResponse(request, DicomStatus.SOPClassNotSupported));

    public Task<DicomNDeleteResponse> OnNDeleteRequestAsync(DicomNDeleteRequest request) =>
        Task.FromResult(new DicomNDeleteResponse(request, DicomStatus.SOPClassNotSupported));

    public Task<DicomNEventReportResponse> OnNEventReportRequestAsync(DicomNEventReportRequest request) =>
        Task.FromResult(new DicomNEventReportResponse(request, DicomStatus.SOPClassNotSupported));

    public Task<DicomNGetResponse> OnNGetRequestAsync(DicomNGetRequest request) =>
        Task.FromResult(new DicomNGetResponse(request, DicomStatus.SOPClassNotSupported));
}
