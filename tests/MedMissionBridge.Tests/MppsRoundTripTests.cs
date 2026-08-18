using System.Net;
using System.Net.Sockets;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using MedMissionBridge.Data;
using MedMissionBridge.Dicom;

namespace MedMissionBridge.Tests;

/// <summary>
/// Drives the bridge the way the acquisition console does: N-CREATE when the exposure
/// starts, N-SET when it ends. This is what closes studies without anyone remembering to.
/// </summary>
public class MppsRoundTripTests
{
    private const string RecordId = "8f14e45f-ceea-467a-9f9c-6a2f0b4c1d33";

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed record Applied(string RecordId, WorklistStatus Status);

    private static List<Applied> CaptureStatuses()
    {
        var applied = new List<Applied>();
        MwlService.ForgetSteps();
        MwlService.StatusSink = (recordId, status) =>
        {
            lock (applied) applied.Add(new Applied(recordId, status));
            return Task.CompletedTask;
        };
        return applied;
    }

    private static DicomDataset StartDataset(string recordId) => new()
    {
        { DicomTag.PatientID, recordId },
        { DicomTag.PerformedProcedureStepStatus, "IN PROGRESS" },
    };

    [Fact]
    public async Task an_exposure_start_and_end_move_the_record_without_anyone_touching_it()
    {
        DicomSetup.EnsureInitialized();
        var applied = CaptureStatuses();
        var port = FreePort();
        using var server = new MwlServer("127.0.0.1", port);
        var stepUid = DicomUID.Generate();

        DicomStatus? createStatus = null, setStatus = null;

        var create = new DicomNCreateRequest(DicomUID.ModalityPerformedProcedureStep, stepUid)
        { Dataset = StartDataset(RecordId) };
        create.OnResponseReceived += (_, resp) => createStatus = resp.Status;

        // The N-SET carries only the step's own UID — the link made at N-CREATE is what
        // tells the bridge which survey is being closed.
        var set = new DicomNSetRequest(DicomUID.ModalityPerformedProcedureStep, stepUid)
        {
            Dataset = new DicomDataset { { DicomTag.PerformedProcedureStepStatus, "COMPLETED" } },
        };
        set.OnResponseReceived += (_, resp) => setStatus = resp.Status;

        var client = DicomClientFactory.Create("127.0.0.1", port, false, "MDVIZIO", "MEDMISSION");
        await client.AddRequestAsync(create);
        await client.AddRequestAsync(set);
        await client.SendAsync();

        Assert.Equal(DicomStatus.Success, createStatus);
        Assert.Equal(DicomStatus.Success, setStatus);
        Assert.Equal(
            [new Applied(RecordId, WorklistStatus.InProgress), new Applied(RecordId, WorklistStatus.Completed)],
            applied);
    }

    [Fact]
    public async Task a_discontinued_step_cancels_rather_than_completes()
    {
        DicomSetup.EnsureInitialized();
        var applied = CaptureStatuses();
        var port = FreePort();
        using var server = new MwlServer("127.0.0.1", port);
        var stepUid = DicomUID.Generate();

        var create = new DicomNCreateRequest(DicomUID.ModalityPerformedProcedureStep, stepUid)
        { Dataset = StartDataset(RecordId) };
        var set = new DicomNSetRequest(DicomUID.ModalityPerformedProcedureStep, stepUid)
        {
            Dataset = new DicomDataset { { DicomTag.PerformedProcedureStepStatus, "DISCONTINUED" } },
        };

        var client = DicomClientFactory.Create("127.0.0.1", port, false, "MDVIZIO", "MEDMISSION");
        await client.AddRequestAsync(create);
        await client.AddRequestAsync(set);
        await client.SendAsync();

        // A patient who left is not a completed study, and the difference matters to
        // whoever counts how many people were actually screened.
        Assert.Equal(WorklistStatus.Cancelled, applied[^1].Status);
    }

    [Fact]
    public async Task a_step_about_an_unknown_study_is_refused_rather_than_acknowledged()
    {
        DicomSetup.EnsureInitialized();
        var applied = CaptureStatuses();
        var port = FreePort();
        using var server = new MwlServer("127.0.0.1", port);

        DicomStatus? status = null;
        var create = new DicomNCreateRequest(
            DicomUID.ModalityPerformedProcedureStep, DicomUID.Generate())
        {
            // Someone else's study UID, no patient id: nothing here names a survey.
            Dataset = new DicomDataset
            {
                { DicomTag.StudyInstanceUID, "1.2.840.113619.2.55.3.99" },
                { DicomTag.PerformedProcedureStepStatus, "IN PROGRESS" },
            },
        };
        create.OnResponseReceived += (_, resp) => status = resp.Status;

        var client = DicomClientFactory.Create("127.0.0.1", port, false, "MDVIZIO", "MEDMISSION");
        await client.AddRequestAsync(create);
        await client.SendAsync();

        // Answering Success would tell the console the step is tracked when it is not.
        Assert.Equal(DicomStatus.NoSuchObjectInstance, status);
        Assert.Empty(applied);
    }

    [Fact]
    public async Task the_worklist_still_answers_on_the_same_association()
    {
        // The console points its worklist and MPPS destinations at one host and port,
        // so both services have to live behind the same AE.
        DicomSetup.EnsureInitialized();
        CaptureStatuses();
        MwlService.WorklistSource = () => Task.FromResult<IReadOnlyList<DicomDataset>>(
            [DicomConversions.BuildWorklistItem(
                new SurveyRecord { RecordId = RecordId, RawJson = "{}", No = "TAB-1" }, new MwlOptions())]);

        var port = FreePort();
        using var server = new MwlServer("127.0.0.1", port);

        var found = new List<DicomDataset>();
        var find = DicomCFindRequest.CreateWorklistQuery();
        find.OnResponseReceived += (_, resp) =>
        {
            if (resp.Status == DicomStatus.Pending && resp.HasDataset) found.Add(resp.Dataset);
        };
        var create = new DicomNCreateRequest(
            DicomUID.ModalityPerformedProcedureStep, DicomUID.Generate())
        { Dataset = StartDataset(RecordId) };

        var client = DicomClientFactory.Create("127.0.0.1", port, false, "MDVIZIO", "MEDMISSION");
        await client.AddRequestAsync(find);
        await client.AddRequestAsync(create);
        await client.SendAsync();

        Assert.Single(found);
    }
}
