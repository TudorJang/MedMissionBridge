using System.Net;
using System.Net.Sockets;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using MedMissionBridge.Data;
using MedMissionBridge.Dicom;

namespace MedMissionBridge.Tests;

public class MwlRoundTripTests
{
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static DicomDataset Item(string recordId, string no) =>
        DicomConversions.BuildWorklistItem(new SurveyRecord
        {
            RecordId = recordId, RawJson = "{}", No = no,
            FirstName = "Juan", LastName = "Dela Cruz", Date = "2026-08-14",
        }, new MwlOptions());

    [Fact]
    public async Task cfind_returns_scheduled_items_and_honors_matching()
    {
        DicomSetup.EnsureInitialized();
        MwlService.WorklistSource = () => Task.FromResult<IReadOnlyList<DicomDataset>>(
            [Item("r1", "TAB-1"), Item("r2", "TAB-2")]);

        var port = FreePort();
        using var server = new MwlServer("127.0.0.1", port);

        var client = DicomClientFactory.Create("127.0.0.1", port, false, "TESTSCU", "MEDMISSION");
        var found = new List<DicomDataset>();
        var request = DicomCFindRequest.CreateWorklistQuery();
        request.Dataset.AddOrUpdate(DicomTag.PatientID, "r2");
        request.OnResponseReceived += (_, resp) =>
        {
            if (resp.Status == DicomStatus.Pending && resp.HasDataset) found.Add(resp.Dataset);
        };
        await client.AddRequestAsync(request);
        await client.SendAsync();

        var hit = Assert.Single(found);
        Assert.Equal("TAB-2", hit.GetSingleValue<string>(DicomTag.AccessionNumber));
    }

    [Fact]
    public async Task cecho_succeeds_for_connectivity_tests()
    {
        DicomSetup.EnsureInitialized();
        MwlService.WorklistSource = () => Task.FromResult<IReadOnlyList<DicomDataset>>([]);
        var port = FreePort();
        using var server = new MwlServer("127.0.0.1", port);

        var client = DicomClientFactory.Create("127.0.0.1", port, false, "TESTSCU", "MEDMISSION");
        DicomStatus? status = null;
        var echo = new DicomCEchoRequest();
        echo.OnResponseReceived += (_, resp) => status = resp.Status;
        await client.AddRequestAsync(echo);
        await client.SendAsync();

        Assert.Equal(DicomStatus.Success, status);
    }

    [Fact]
    public async Task cfind_round_trips_non_ascii_patient_names()
    {
        DicomSetup.EnsureInitialized();
        var item = DicomConversions.BuildWorklistItem(new SurveyRecord
        {
            RecordId = "nx1", RawJson = "{}", No = "TAB-NX",
            FirstName = "Juán", LastName = "Peña", Date = "2026-08-14",
        }, new MwlOptions());
        MwlService.WorklistSource = () => Task.FromResult<IReadOnlyList<DicomDataset>>([item]);

        var port = FreePort();
        using var server = new MwlServer("127.0.0.1", port);

        var client = DicomClientFactory.Create("127.0.0.1", port, false, "TESTSCU", "MEDMISSION");
        var found = new List<DicomDataset>();
        var request = DicomCFindRequest.CreateWorklistQuery();
        request.Dataset.AddOrUpdate(DicomTag.PatientID, "nx1");
        request.OnResponseReceived += (_, resp) =>
        {
            if (resp.Status == DicomStatus.Pending && resp.HasDataset) found.Add(resp.Dataset);
        };
        await client.AddRequestAsync(request);
        await client.SendAsync();

        var hit = Assert.Single(found);
        Assert.Equal("Peña^Juán", hit.GetSingleValue<string>(DicomTag.PatientName));
    }
}
