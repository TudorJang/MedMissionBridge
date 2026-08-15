# MedMission Bridge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the Windows laptop bridge that receives tablet surveys over HTTP, manages a worklist with a loopback-only web UI, and answers DICOM MWL C-FIND queries from the co-located medical software.

**Architecture:** One .NET 9 process hosting four units behind narrow interfaces: an ASP.NET Core (Kestrel) ingest/lookup API, a SQLite store (EF Core), a fo-dicom MWL SCP, and an mDNS advertiser, plus static web UI files served by the same Kestrel. The MWL SCP and UI depend only on the store; the raw tablet JSON is preserved verbatim and is the single data contract (`wire-contract.md` in the tablet repo).

**Tech Stack:** .NET 9 (SDK 9.0.316, pinned via global.json), ASP.NET Core minimal APIs, EF Core 9 + SQLite, fo-dicom 5.1.2, Makaretu.Dns.Multicast, Serilog, xUnit + WebApplicationFactory.

**Spec:** `docs/superpowers/specs/2026-08-15-medmission-bridge-design.md`

## Global Constraints

- Repo root is `D:\MedMissionBridge`; all commands run there unless stated.
- SDK pinned to `9.0.316` (the machine also has 10/11 previews — global.json prevents drift).
- Ingest contract is the tablet repo's `D:\MedMissionSurveyApp\docs\reference\wire-contract.md`: upsert by `recordId`, 2xx = success, absent-not-null JSON, ISO-8601 dates.
- A re-sent payload updates survey data but **never changes `Status` or `ReceivedAtUtc`** (spec §5).
- MWL exposes only `RECEIVED` and `IN_PROGRESS` records (spec §7).
- `/api/v1/*` is LAN-facing and gated by `X-Api-Key`; everything else (UI pages, `/api/ui/*`) is loopback-only (spec §6).
- Default ports: HTTP 18080, MWL 11112; AE Title `MEDMISSION`; Modality `CR` — all configurable under the `Bridge` config section (spec §7).
- No TLS, no MPPS, no MWL private tag in v1 (spec §12).
- TDD for all logic; Compose-free plain HTML/JS for the UI (no build step).

---

### Task 1: Solution scaffold, configuration and logging

**Files:**
- Create: `global.json`, `.gitignore`, `MedMissionBridge.sln`
- Create: `src/MedMissionBridge/MedMissionBridge.csproj`
- Create: `src/MedMissionBridge/BridgeOptions.cs`
- Create: `src/MedMissionBridge/appsettings.json`
- Create: `src/MedMissionBridge/Program.cs`
- Create: `tests/MedMissionBridge.Tests/MedMissionBridge.Tests.csproj`
- Test: `tests/MedMissionBridge.Tests/BridgeOptionsTests.cs`

**Interfaces:**
- Produces: `BridgeOptions` (bound from config section `"Bridge"`) with properties `ApiKey`, `HttpPort`, `DbPath`, `Mwl.Port`, `Mwl.AeTitle`, `Mwl.Modality`, `Mwl.StationAeTitle`, `Mwl.ProcedureDescription`, `Mdns.ServiceName`, and method `ResolveDbPath()`/`ResolveDataDir()`. `Program` is `public partial` for `WebApplicationFactory`. Program.cs contains anchor comments (`// [ANCHOR:*]`) that later tasks insert code after — do not remove them.

- [ ] **Step 1: Scaffold**

```bash
cd /d/MedMissionBridge
cat > global.json <<'EOF'
{ "sdk": { "version": "9.0.316" } }
EOF
dotnet new gitignore
dotnet new sln -n MedMissionBridge
dotnet new web -o src/MedMissionBridge -n MedMissionBridge
dotnet new xunit -o tests/MedMissionBridge.Tests -n MedMissionBridge.Tests
dotnet sln add src/MedMissionBridge tests/MedMissionBridge.Tests
dotnet add tests/MedMissionBridge.Tests reference src/MedMissionBridge
dotnet add src/MedMissionBridge package Microsoft.EntityFrameworkCore.Sqlite --version 9.0.8
dotnet add src/MedMissionBridge package Microsoft.EntityFrameworkCore.Design --version 9.0.8
dotnet add src/MedMissionBridge package fo-dicom --version 5.1.2
dotnet add src/MedMissionBridge package Makaretu.Dns.Multicast --version 0.31.3
dotnet add src/MedMissionBridge package Serilog.AspNetCore --version 8.0.2
dotnet add src/MedMissionBridge package Serilog.Sinks.File --version 6.0.0
dotnet add tests/MedMissionBridge.Tests package Microsoft.AspNetCore.Mvc.Testing --version 9.0.8
```

If a pinned version fails to restore, use the nearest available patch of the same major.minor and note it in your report.

- [ ] **Step 2: Write `BridgeOptions.cs`**

```csharp
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
```

- [ ] **Step 3: Write `appsettings.json`** (replace the generated one)

```json
{
  "Bridge": {
    "ApiKey": "changeme-dev-key",
    "HttpPort": 18080,
    "DbPath": "",
    "Mwl": {
      "Port": 11112,
      "AeTitle": "MEDMISSION",
      "Modality": "CR",
      "StationAeTitle": "MEDMISSION",
      "ProcedureDescription": "TB Screening Chest X-Ray"
    },
    "Mdns": { "ServiceName": "" }
  },
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },
  "AllowedHosts": "*"
}
```

- [ ] **Step 4: Write `Program.cs`** (replace the generated one)

```csharp
using MedMissionBridge;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
var bridge = builder.Configuration.GetSection("Bridge").Get<BridgeOptions>() ?? new BridgeOptions();
builder.Services.AddSingleton(bridge);

var isTesting = builder.Environment.IsEnvironment("Testing");
if (!isTesting)
{
    Log.Logger = new LoggerConfiguration()
        .WriteTo.Console()
        .WriteTo.File(Path.Combine(bridge.ResolveDataDir(), "logs", "bridge-.log"),
            rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30)
        .CreateLogger();
    builder.Host.UseSerilog();
    builder.WebHost.UseUrls($"http://0.0.0.0:{bridge.HttpPort}");
}

// [ANCHOR:SERVICES] later tasks register services below this line

var app = builder.Build();

// [ANCHOR:MIGRATE] database migration on startup goes here
// [ANCHOR:MIDDLEWARE] loopback gate goes here
// [ANCHOR:STATIC] static web UI goes here
// [ANCHOR:ENDPOINTS] API endpoints go here
// [ANCHOR:SERVERS] MWL server and mDNS advertiser go here (guarded by !isTesting)

app.Run();

public partial class Program;
```

- [ ] **Step 5: Write the config test** `tests/MedMissionBridge.Tests/BridgeOptionsTests.cs`

```csharp
using MedMissionBridge;

namespace MedMissionBridge.Tests;

public class BridgeOptionsTests
{
    [Fact]
    public void defaults_match_the_spec()
    {
        var o = new BridgeOptions();
        Assert.Equal(18080, o.HttpPort);
        Assert.Equal(11112, o.Mwl.Port);
        Assert.Equal("MEDMISSION", o.Mwl.AeTitle);
        Assert.Equal("CR", o.Mwl.Modality);
    }

    [Fact]
    public void empty_db_path_resolves_under_program_data()
    {
        var o = new BridgeOptions();
        Assert.EndsWith(Path.Combine("MedMissionBridge", "bridge.db"), o.ResolveDbPath());
    }

    [Fact]
    public void explicit_db_path_wins()
    {
        var o = new BridgeOptions { DbPath = @"C:\tmp\x.db" };
        Assert.Equal(@"C:\tmp\x.db", o.ResolveDbPath());
    }
}
```

- [ ] **Step 6: Build and test**

Run: `dotnet test`
Expected: build succeeds, 3 tests pass (plus the template's default test if present — delete `UnitTest1.cs`).

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "chore: scaffold .NET 9 solution with config, logging and test host"
```

---

### Task 2: Payload extractor (pure)

**Files:**
- Create: `src/MedMissionBridge/Ingest/PayloadExtractor.cs`
- Test: `tests/MedMissionBridge.Tests/PayloadExtractorTests.cs`

**Interfaces:**
- Produces: `record ExtractedSurvey(string RecordId, string? No, string? Date, string? FirstName, string? LastName, string? BirthDate, string? Gender, string? Region, string? Province, string? City, string? Barangay, string? Zip, string? Address)` and `static bool PayloadExtractor.TryExtract(string json, out ExtractedSurvey? extracted)` — `false` for unparseable JSON or missing/empty `recordId`. Consumed by Tasks 3 and 5.

- [ ] **Step 1: Write the failing tests**

```csharp
using MedMissionBridge.Ingest;

namespace MedMissionBridge.Tests;

public class PayloadExtractorTests
{
    private const string Full = """
        {"recordId":"3f8b1c2e-9a4d-4c11-8e77-2b6a0d5f9c31","no":"TAB-3FBB-0001",
         "date":"2026-08-14",
         "patient":{"firstName":"Juan","lastName":"Dela Cruz","birthDate":"1980-03-04",
           "gender":"MALE","address":"12 Mabini St","region":"NATIONAL CAPITAL REGION (NCR)",
           "city":"City Of Manila","barangay":"Ermita","zip":"1000"},
         "medicalHistory":{},"vitalSigns":{},"symptoms":[],"tbInfo":{},"smoking":{},
         "alcohol":{},"environmentalExposure":{}}
        """;

    [Fact]
    public void extracts_worklist_fields_from_a_full_payload()
    {
        Assert.True(PayloadExtractor.TryExtract(Full, out var e));
        Assert.Equal("3f8b1c2e-9a4d-4c11-8e77-2b6a0d5f9c31", e!.RecordId);
        Assert.Equal("TAB-3FBB-0001", e.No);
        Assert.Equal("2026-08-14", e.Date);
        Assert.Equal("Juan", e.FirstName);
        Assert.Equal("Dela Cruz", e.LastName);
        Assert.Equal("1980-03-04", e.BirthDate);
        Assert.Equal("MALE", e.Gender);
        Assert.Equal("NATIONAL CAPITAL REGION (NCR)", e.Region);
        Assert.Null(e.Province); // absent-not-null: NCR has no province key at all
        Assert.Equal("City Of Manila", e.City);
        Assert.Equal("Ermita", e.Barangay);
        Assert.Equal("1000", e.Zip);
        Assert.Equal("12 Mabini St", e.Address);
    }

    [Fact]
    public void a_blank_survey_still_extracts_its_record_id()
    {
        var json = """{"recordId":"abc","patient":{},"medicalHistory":{},"vitalSigns":{},"symptoms":[],"tbInfo":{},"smoking":{},"alcohol":{},"environmentalExposure":{}}""";
        Assert.True(PayloadExtractor.TryExtract(json, out var e));
        Assert.Equal("abc", e!.RecordId);
        Assert.Null(e.No);
        Assert.Null(e.FirstName);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"recordId":""}""")]
    [InlineData("""{"recordId":null}""")]
    public void rejects_payloads_without_a_usable_record_id(string json)
    {
        Assert.False(PayloadExtractor.TryExtract(json, out var e));
        Assert.Null(e);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter PayloadExtractorTests`
Expected: FAIL — `PayloadExtractor` does not exist (compile error is the RED here).

- [ ] **Step 3: Implement**

```csharp
using System.Text.Json;

namespace MedMissionBridge.Ingest;

public record ExtractedSurvey(
    string RecordId, string? No, string? Date,
    string? FirstName, string? LastName, string? BirthDate, string? Gender,
    string? Region, string? Province, string? City, string? Barangay,
    string? Zip, string? Address);

public static class PayloadExtractor
{
    public static bool TryExtract(string json, out ExtractedSurvey? extracted)
    {
        extracted = null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return false; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var recordId = Str(root, "recordId");
            if (string.IsNullOrEmpty(recordId)) return false;

            root.TryGetProperty("patient", out var patient);
            extracted = new ExtractedSurvey(
                RecordId: recordId,
                No: Str(root, "no"),
                Date: Str(root, "date"),
                FirstName: Str(patient, "firstName"),
                LastName: Str(patient, "lastName"),
                BirthDate: Str(patient, "birthDate"),
                Gender: Str(patient, "gender"),
                Region: Str(patient, "region"),
                Province: Str(patient, "province"),
                City: Str(patient, "city"),
                Barangay: Str(patient, "barangay"),
                Zip: Str(patient, "zip"),
                Address: Str(patient, "address"));
            return true;
        }
    }

    private static string? Str(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out var p)
            && p.ValueKind == JsonValueKind.String
            && p.GetString() is { Length: > 0 } s
        ? s : null;
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter PayloadExtractorTests`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: extract worklist fields from the tablet wire payload"
```

---

### Task 3: Data layer — entity, context, migration, upsert

**Files:**
- Create: `src/MedMissionBridge/Data/SurveyRecord.cs`
- Create: `src/MedMissionBridge/Data/BridgeDbContext.cs`
- Create: `src/MedMissionBridge/Data/DesignTimeDbContextFactory.cs`
- Create: `src/MedMissionBridge/Data/SurveyStore.cs` (Upsert only in this task)
- Create: `src/MedMissionBridge/Migrations/*` (generated)
- Modify: `src/MedMissionBridge/Program.cs` (anchors `SERVICES`, `MIGRATE`)
- Test: `tests/MedMissionBridge.Tests/SurveyStoreTests.cs`

**Interfaces:**
- Consumes: `ExtractedSurvey` (Task 2).
- Produces: `enum WorklistStatus { Received, InProgress, Completed, Cancelled }`; entity `SurveyRecord` with `RecordId, ReceivedAtUtc, UpdatedAtUtc, Status, No, Date, FirstName, LastName, BirthDate, Gender, Region, Province, City, Barangay, Zip, Address, RawJson`; `SurveyStore(IDbContextFactory<BridgeDbContext>)` with `Task UpsertAsync(ExtractedSurvey e, string rawJson)`. Later tasks add query methods to this same class.

- [ ] **Step 1: Write the failing tests**

```csharp
using MedMissionBridge.Data;
using MedMissionBridge.Ingest;
using Microsoft.EntityFrameworkCore;

namespace MedMissionBridge.Tests;

public sealed class SurveyStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"bridge-test-{Guid.NewGuid():N}.db");
    private readonly PooledDbContextFactory<BridgeDbContext> _factory;
    private readonly SurveyStore _store;

    public SurveyStoreTests()
    {
        var options = new DbContextOptionsBuilder<BridgeDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options;
        _factory = new PooledDbContextFactory<BridgeDbContext>(options);
        using var ctx = _factory.CreateDbContext();
        ctx.Database.Migrate();
        _store = new SurveyStore(_factory);
    }

    public void Dispose()
    {
        using (var ctx = _factory.CreateDbContext()) ctx.Database.EnsureDeleted();
    }

    private static ExtractedSurvey Extracted(string id, string? first = "Juan") =>
        new(id, "TAB-3FBB-0001", "2026-08-14", first, "Dela Cruz", "1980-03-04",
            "MALE", "NCR", null, "City Of Manila", "Ermita", "1000", "12 Mabini St");

    [Fact]
    public async Task insert_starts_as_received_and_stores_raw_json()
    {
        await _store.UpsertAsync(Extracted("r1"), """{"recordId":"r1"}""");
        using var ctx = _factory.CreateDbContext();
        var r = await ctx.Surveys.SingleAsync(x => x.RecordId == "r1");
        Assert.Equal(WorklistStatus.Received, r.Status);
        Assert.Equal("""{"recordId":"r1"}""", r.RawJson);
        Assert.Equal("Juan", r.FirstName);
    }

    [Fact]
    public async Task resend_updates_data_but_preserves_status_and_received_time()
    {
        await _store.UpsertAsync(Extracted("r1"), "v1");
        DateTime firstReceived;
        using (var ctx = _factory.CreateDbContext())
        {
            var r = await ctx.Surveys.SingleAsync(x => x.RecordId == "r1");
            firstReceived = r.ReceivedAtUtc;
            r.Status = WorklistStatus.Completed;
            await ctx.SaveChangesAsync();
        }

        await _store.UpsertAsync(Extracted("r1", first: "Maria"), "v2");

        using (var ctx = _factory.CreateDbContext())
        {
            var r = await ctx.Surveys.SingleAsync(x => x.RecordId == "r1");
            Assert.Equal("Maria", r.FirstName);        // data updated
            Assert.Equal("v2", r.RawJson);
            Assert.Equal(WorklistStatus.Completed, r.Status); // status preserved
            Assert.Equal(firstReceived, r.ReceivedAtUtc);     // audit preserved
            Assert.True(r.UpdatedAtUtc >= firstReceived);
        }
    }
}
```

Add to the test project (needed for `PooledDbContextFactory`):

```bash
dotnet add tests/MedMissionBridge.Tests package Microsoft.EntityFrameworkCore.Sqlite --version 9.0.8
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter SurveyStoreTests`
Expected: FAIL — types don't exist.

- [ ] **Step 3: Implement entity + context**

`src/MedMissionBridge/Data/SurveyRecord.cs`:

```csharp
namespace MedMissionBridge.Data;

public enum WorklistStatus { Received, InProgress, Completed, Cancelled }

public class SurveyRecord
{
    public required string RecordId { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public WorklistStatus Status { get; set; } = WorklistStatus.Received;
    public string? No { get; set; }
    public string? Date { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? BirthDate { get; set; }
    public string? Gender { get; set; }
    public string? Region { get; set; }
    public string? Province { get; set; }
    public string? City { get; set; }
    public string? Barangay { get; set; }
    public string? Zip { get; set; }
    public string? Address { get; set; }
    public required string RawJson { get; set; }
}
```

`src/MedMissionBridge/Data/BridgeDbContext.cs`:

```csharp
using Microsoft.EntityFrameworkCore;

namespace MedMissionBridge.Data;

public class BridgeDbContext(DbContextOptions<BridgeDbContext> options) : DbContext(options)
{
    public DbSet<SurveyRecord> Surveys => Set<SurveyRecord>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        var e = mb.Entity<SurveyRecord>();
        e.HasKey(x => x.RecordId);
        e.Property(x => x.Status).HasConversion<string>();
        e.HasIndex(x => x.No);
        e.HasIndex(x => x.Status);
    }
}
```

`src/MedMissionBridge/Data/DesignTimeDbContextFactory.cs` (lets `dotnet ef` build the context without running Program):

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MedMissionBridge.Data;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<BridgeDbContext>
{
    public BridgeDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<BridgeDbContext>()
            .UseSqlite("Data Source=design-time.db").Options);
}
```

`src/MedMissionBridge/Data/SurveyStore.cs`:

```csharp
using MedMissionBridge.Ingest;
using Microsoft.EntityFrameworkCore;

namespace MedMissionBridge.Data;

public class SurveyStore(IDbContextFactory<BridgeDbContext> factory)
{
    public async Task UpsertAsync(ExtractedSurvey e, string rawJson)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        var existing = await ctx.Surveys.FindAsync(e.RecordId);
        if (existing is null)
        {
            ctx.Surveys.Add(new SurveyRecord
            {
                RecordId = e.RecordId, ReceivedAtUtc = now, UpdatedAtUtc = now,
                Status = WorklistStatus.Received, RawJson = rawJson,
                No = e.No, Date = e.Date, FirstName = e.FirstName, LastName = e.LastName,
                BirthDate = e.BirthDate, Gender = e.Gender, Region = e.Region,
                Province = e.Province, City = e.City, Barangay = e.Barangay,
                Zip = e.Zip, Address = e.Address,
            });
        }
        else
        {
            // A tablet edit-and-resend refreshes the survey but must not change
            // Status or ReceivedAtUtc: a completed study must not silently
            // reappear on the modality worklist (spec section 5).
            existing.UpdatedAtUtc = now; existing.RawJson = rawJson;
            existing.No = e.No; existing.Date = e.Date;
            existing.FirstName = e.FirstName; existing.LastName = e.LastName;
            existing.BirthDate = e.BirthDate; existing.Gender = e.Gender;
            existing.Region = e.Region; existing.Province = e.Province;
            existing.City = e.City; existing.Barangay = e.Barangay;
            existing.Zip = e.Zip; existing.Address = e.Address;
        }
        await ctx.SaveChangesAsync();
    }
}
```

- [ ] **Step 4: Generate the migration**

```bash
dotnet new tool-manifest --force
dotnet tool install dotnet-ef --version 9.0.8
dotnet ef migrations add Initial --project src/MedMissionBridge
```

- [ ] **Step 5: Wire Program.cs**

Insert after `// [ANCHOR:SERVICES]`:

```csharp
builder.Services.AddDbContextFactory<MedMissionBridge.Data.BridgeDbContext>(o =>
    o.UseSqlite($"Data Source={bridge.ResolveDbPath()}"));
builder.Services.AddSingleton<MedMissionBridge.Data.SurveyStore>();
```

Insert after `// [ANCHOR:MIGRATE]`:

```csharp
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider
        .GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<MedMissionBridge.Data.BridgeDbContext>>();
    using var db = dbFactory.CreateDbContext();
    db.Database.Migrate();
}
```

Add `using Microsoft.EntityFrameworkCore;` to Program.cs's usings.

- [ ] **Step 6: Run tests**

Run: `dotnet test`
Expected: PASS (all).

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "feat: SQLite store with status-preserving upsert"
```

---

### Task 4: Store queries and status rules

**Files:**
- Create: `src/MedMissionBridge/Data/StatusRules.cs`
- Modify: `src/MedMissionBridge/Data/SurveyStore.cs` (append methods)
- Test: `tests/MedMissionBridge.Tests/StatusRulesTests.cs`, append to `SurveyStoreTests.cs`

**Interfaces:**
- Produces (on `SurveyStore`):
  - `Task<SurveyRecord?> GetAsync(string recordId)`
  - `Task<SurveyRecord?> GetByAccessionAsync(string no)` — latest `UpdatedAtUtc` wins
  - `Task<IReadOnlyList<SurveyRecord>> ListAsync(string? search, WorklistStatus? status)` — newest-first (`ReceivedAtUtc` desc); search is case-insensitive contains over `FirstName`, `LastName`, `No`, `City`
  - `Task<IReadOnlyList<SurveyRecord>> GetScheduledAsync()` — `Received` + `InProgress` only
  - `Task<StatusChangeResult> TryChangeStatusAsync(string recordId, WorklistStatus to)` where `enum StatusChangeResult { Changed, NotFound, InvalidTransition }`
- Produces: `static bool StatusRules.CanTransition(WorklistStatus from, WorklistStatus to)`.

Transition set (spec §5 plus explicit undo paths for mis-taps — the operator works on a tablet-adjacent laptop in the field; a one-tap mistake must be recoverable without editing the database):
`Received→InProgress`, `Received→Completed`, `Received→Cancelled`, `InProgress→Completed`, `InProgress→Cancelled`, `InProgress→Received`, `Completed→InProgress`, `Cancelled→Received`. Everything else is invalid.

- [ ] **Step 1: Write the failing tests**

`tests/MedMissionBridge.Tests/StatusRulesTests.cs`:

```csharp
using MedMissionBridge.Data;

namespace MedMissionBridge.Tests;

public class StatusRulesTests
{
    [Theory]
    [InlineData(WorklistStatus.Received, WorklistStatus.InProgress, true)]
    [InlineData(WorklistStatus.Received, WorklistStatus.Completed, true)]
    [InlineData(WorklistStatus.Received, WorklistStatus.Cancelled, true)]
    [InlineData(WorklistStatus.InProgress, WorklistStatus.Completed, true)]
    [InlineData(WorklistStatus.InProgress, WorklistStatus.Cancelled, true)]
    [InlineData(WorklistStatus.InProgress, WorklistStatus.Received, true)]
    [InlineData(WorklistStatus.Completed, WorklistStatus.InProgress, true)]
    [InlineData(WorklistStatus.Cancelled, WorklistStatus.Received, true)]
    [InlineData(WorklistStatus.Completed, WorklistStatus.Received, false)]
    [InlineData(WorklistStatus.Completed, WorklistStatus.Cancelled, false)]
    [InlineData(WorklistStatus.Cancelled, WorklistStatus.Completed, false)]
    [InlineData(WorklistStatus.Received, WorklistStatus.Received, false)]
    public void transition_table(WorklistStatus from, WorklistStatus to, bool allowed) =>
        Assert.Equal(allowed, StatusRules.CanTransition(from, to));
}
```

Append to `SurveyStoreTests.cs`:

```csharp
    [Fact]
    public async Task scheduled_excludes_completed_and_cancelled()
    {
        await _store.UpsertAsync(Extracted("a"), "{}");
        await _store.UpsertAsync(Extracted("b"), "{}");
        await _store.UpsertAsync(Extracted("c"), "{}");
        Assert.Equal(StatusChangeResult.Changed, await _store.TryChangeStatusAsync("b", WorklistStatus.InProgress));
        Assert.Equal(StatusChangeResult.Changed, await _store.TryChangeStatusAsync("c", WorklistStatus.Completed));

        var scheduled = await _store.GetScheduledAsync();
        Assert.Equal(new[] { "a", "b" }, scheduled.Select(r => r.RecordId).OrderBy(x => x));
    }

    [Fact]
    public async Task invalid_transition_is_rejected_and_unknown_id_reported()
    {
        await _store.UpsertAsync(Extracted("a"), "{}");
        Assert.Equal(StatusChangeResult.Changed, await _store.TryChangeStatusAsync("a", WorklistStatus.Completed));
        Assert.Equal(StatusChangeResult.InvalidTransition, await _store.TryChangeStatusAsync("a", WorklistStatus.Cancelled));
        Assert.Equal(StatusChangeResult.NotFound, await _store.TryChangeStatusAsync("nope", WorklistStatus.Completed));
    }

    [Fact]
    public async Task search_matches_name_no_and_city_case_insensitively()
    {
        await _store.UpsertAsync(Extracted("a"), "{}");
        Assert.Single(await _store.ListAsync("juan", null));
        Assert.Single(await _store.ListAsync("3fbb", null));
        Assert.Single(await _store.ListAsync("manila", null));
        Assert.Empty(await _store.ListAsync("zzz", null));
        Assert.Single(await _store.ListAsync(null, WorklistStatus.Received));
        Assert.Empty(await _store.ListAsync(null, WorklistStatus.Completed));
    }

    [Fact]
    public async Task accession_lookup_returns_latest_updated_match()
    {
        await _store.UpsertAsync(Extracted("a"), "{}");
        await Task.Delay(10);
        await _store.UpsertAsync(Extracted("b"), "{}"); // same No in the fixture
        var hit = await _store.GetByAccessionAsync("TAB-3FBB-0001");
        Assert.Equal("b", hit!.RecordId);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter "StatusRulesTests|SurveyStoreTests"`
Expected: FAIL — new members don't exist.

- [ ] **Step 3: Implement**

`src/MedMissionBridge/Data/StatusRules.cs`:

```csharp
namespace MedMissionBridge.Data;

public enum StatusChangeResult { Changed, NotFound, InvalidTransition }

public static class StatusRules
{
    public static bool CanTransition(WorklistStatus from, WorklistStatus to) => (from, to) switch
    {
        (WorklistStatus.Received, WorklistStatus.InProgress) => true,
        (WorklistStatus.Received, WorklistStatus.Completed) => true,
        (WorklistStatus.Received, WorklistStatus.Cancelled) => true,
        (WorklistStatus.InProgress, WorklistStatus.Completed) => true,
        (WorklistStatus.InProgress, WorklistStatus.Cancelled) => true,
        // Undo paths: a mis-tap in the field must be recoverable from the UI.
        (WorklistStatus.InProgress, WorklistStatus.Received) => true,
        (WorklistStatus.Completed, WorklistStatus.InProgress) => true,
        (WorklistStatus.Cancelled, WorklistStatus.Received) => true,
        _ => false,
    };
}
```

Append to `SurveyStore`:

```csharp
    public async Task<SurveyRecord?> GetAsync(string recordId)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        return await ctx.Surveys.AsNoTracking().SingleOrDefaultAsync(x => x.RecordId == recordId);
    }

    public async Task<SurveyRecord?> GetByAccessionAsync(string no)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        return await ctx.Surveys.AsNoTracking().Where(x => x.No == no)
            .OrderByDescending(x => x.UpdatedAtUtc).FirstOrDefaultAsync();
    }

    public async Task<IReadOnlyList<SurveyRecord>> ListAsync(string? search, WorklistStatus? status)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var q = ctx.Surveys.AsNoTracking();
        if (status is { } s) q = q.Where(x => x.Status == s);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            q = q.Where(x =>
                EF.Functions.Like(x.FirstName!, term) || EF.Functions.Like(x.LastName!, term) ||
                EF.Functions.Like(x.No!, term) || EF.Functions.Like(x.City!, term));
        }
        return await q.OrderByDescending(x => x.ReceivedAtUtc).ToListAsync();
    }

    public async Task<IReadOnlyList<SurveyRecord>> GetScheduledAsync()
    {
        await using var ctx = await factory.CreateDbContextAsync();
        return await ctx.Surveys.AsNoTracking()
            .Where(x => x.Status == WorklistStatus.Received || x.Status == WorklistStatus.InProgress)
            .OrderByDescending(x => x.ReceivedAtUtc).ToListAsync();
    }

    public async Task<StatusChangeResult> TryChangeStatusAsync(string recordId, WorklistStatus to)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var r = await ctx.Surveys.FindAsync(recordId);
        if (r is null) return StatusChangeResult.NotFound;
        if (!StatusRules.CanTransition(r.Status, to)) return StatusChangeResult.InvalidTransition;
        r.Status = to; r.UpdatedAtUtc = DateTime.UtcNow;
        await ctx.SaveChangesAsync();
        return StatusChangeResult.Changed;
    }
```

(`EF.Functions.Like` on SQLite is case-insensitive for ASCII by default, which is what the test asserts.)

- [ ] **Step 4: Run tests** — `dotnet test`, expected PASS (all).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: worklist queries and status transition rules"
```

---

### Task 5: Ingest endpoint (LAN-facing POST)

**Files:**
- Create: `src/MedMissionBridge/Ingest/ApiKeyGate.cs`
- Create: `src/MedMissionBridge/Ingest/IngestEndpoints.cs`
- Modify: `src/MedMissionBridge/Program.cs` (anchor `ENDPOINTS`)
- Test: `tests/MedMissionBridge.Tests/IngestApiTests.cs`, `tests/MedMissionBridge.Tests/BridgeAppFactory.cs`

**Interfaces:**
- Consumes: `PayloadExtractor`, `SurveyStore.UpsertAsync`.
- Produces: `static bool ApiKeyGate.Allows(HttpRequest request, string configuredKey)`; `static void IngestEndpoints.Map(WebApplication app)` mapping `POST /api/v1/surveys` (Task 6 adds the GETs to the same class); test helper `BridgeAppFactory : WebApplicationFactory<Program>` with a temp DB, environment `Testing`, ApiKey `test-key` — reused by Tasks 6, 7.

- [ ] **Step 1: Write the test factory** `tests/MedMissionBridge.Tests/BridgeAppFactory.cs`

```csharp
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MedMissionBridge.Tests;

public sealed class BridgeAppFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "test-key";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"bridge-web-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Bridge:ApiKey", ApiKey);
        builder.UseSetting("Bridge:DbPath", _dbPath);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { File.Delete(_dbPath); } catch (IOException) { }
    }
}
```

- [ ] **Step 2: Write the failing tests** `tests/MedMissionBridge.Tests/IngestApiTests.cs`

```csharp
using System.Net;
using System.Net.Http.Headers;

namespace MedMissionBridge.Tests;

public class IngestApiTests
{
    private const string Payload = """{"recordId":"r-100","no":"TAB-1","patient":{"firstName":"Juan"},"medicalHistory":{},"vitalSigns":{},"symptoms":[],"tbInfo":{},"smoking":{},"alcohol":{},"environmentalExposure":{}}""";

    private static HttpRequestMessage Post(string body, string? key = BridgeAppFactory.ApiKey)
    {
        var m = new HttpRequestMessage(HttpMethod.Post, "/api/v1/surveys")
        { Content = new StringContent(body) };
        m.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (key != null) m.Headers.Add("X-Api-Key", key);
        return m;
    }

    [Fact]
    public async Task accepts_a_valid_payload_and_is_idempotent()
    {
        using var app = new BridgeAppFactory();
        var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post(Payload))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post(Payload))).StatusCode);
    }

    [Fact]
    public async Task wrong_or_missing_key_is_401()
    {
        using var app = new BridgeAppFactory();
        var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Post(Payload, "bad"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Post(Payload, null))).StatusCode);
    }

    [Fact]
    public async Task body_without_record_id_is_400()
    {
        using var app = new BridgeAppFactory();
        var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Post("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Post("not json"))).StatusCode);
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test --filter IngestApiTests`
Expected: FAIL — 404s (endpoint not mapped yet).

- [ ] **Step 4: Implement**

`src/MedMissionBridge/Ingest/ApiKeyGate.cs`:

```csharp
namespace MedMissionBridge.Ingest;

public static class ApiKeyGate
{
    public static bool Allows(HttpRequest request, string configuredKey) =>
        request.Headers.TryGetValue("X-Api-Key", out var got) && got == configuredKey;
}
```

`src/MedMissionBridge/Ingest/IngestEndpoints.cs`:

```csharp
using MedMissionBridge.Data;

namespace MedMissionBridge.Ingest;

public static class IngestEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/v1/surveys", async (HttpRequest request, SurveyStore store, BridgeOptions options) =>
        {
            if (!ApiKeyGate.Allows(request, options.ApiKey)) return Results.Unauthorized();
            using var reader = new StreamReader(request.Body);
            var json = await reader.ReadToEndAsync();
            if (!PayloadExtractor.TryExtract(json, out var extracted)) return Results.BadRequest();
            await store.UpsertAsync(extracted!, json);
            return Results.Ok();
        });
    }
}
```

Insert after `// [ANCHOR:ENDPOINTS]` in Program.cs:

```csharp
MedMissionBridge.Ingest.IngestEndpoints.Map(app);
```

- [ ] **Step 5: Run tests** — `dotnet test`, expected PASS (all).

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat: LAN-facing survey ingest endpoint with API key gate"
```

---

### Task 6: Survey lookup endpoints (LAN-facing GET)

**Files:**
- Modify: `src/MedMissionBridge/Ingest/IngestEndpoints.cs`
- Test: append to `tests/MedMissionBridge.Tests/IngestApiTests.cs`

**Interfaces:**
- Consumes: `SurveyStore.GetAsync`, `GetByAccessionAsync`.
- Produces: `GET /api/v1/surveys/{recordId}` and `GET /api/v1/surveys?accession={no}` returning the stored raw JSON byte-identical, `Content-Type: application/json`; 401 without key, 404 unknown, 400 when the query form lacks `accession`.

- [ ] **Step 1: Write the failing tests** (append to `IngestApiTests.cs`)

```csharp
    [Fact]
    public async Task lookup_returns_stored_json_byte_identical()
    {
        using var app = new BridgeAppFactory();
        var client = app.CreateClient();
        await client.SendAsync(Post(Payload));

        var byId = new HttpRequestMessage(HttpMethod.Get, "/api/v1/surveys/r-100");
        byId.Headers.Add("X-Api-Key", BridgeAppFactory.ApiKey);
        var r1 = await client.SendAsync(byId);
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal(Payload, await r1.Content.ReadAsStringAsync());

        var byAcc = new HttpRequestMessage(HttpMethod.Get, "/api/v1/surveys?accession=TAB-1");
        byAcc.Headers.Add("X-Api-Key", BridgeAppFactory.ApiKey);
        var r2 = await client.SendAsync(byAcc);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal(Payload, await r2.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task lookup_edge_cases()
    {
        using var app = new BridgeAppFactory();
        var client = app.CreateClient();

        var noKey = await client.GetAsync("/api/v1/surveys/r-100");
        Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);

        var unknown = new HttpRequestMessage(HttpMethod.Get, "/api/v1/surveys/none");
        unknown.Headers.Add("X-Api-Key", BridgeAppFactory.ApiKey);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(unknown)).StatusCode);

        var noAccession = new HttpRequestMessage(HttpMethod.Get, "/api/v1/surveys");
        noAccession.Headers.Add("X-Api-Key", BridgeAppFactory.ApiKey);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(noAccession)).StatusCode);
    }
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter IngestApiTests`, expected FAIL (404 on lookups).

- [ ] **Step 3: Implement** (append inside `IngestEndpoints.Map`)

```csharp
        app.MapGet("/api/v1/surveys/{recordId}", async (string recordId, HttpRequest request, SurveyStore store, BridgeOptions options) =>
        {
            if (!ApiKeyGate.Allows(request, options.ApiKey)) return Results.Unauthorized();
            var record = await store.GetAsync(recordId);
            return record is null ? Results.NotFound()
                : Results.Content(record.RawJson, "application/json");
        });

        app.MapGet("/api/v1/surveys", async (string? accession, HttpRequest request, SurveyStore store, BridgeOptions options) =>
        {
            if (!ApiKeyGate.Allows(request, options.ApiKey)) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(accession)) return Results.BadRequest();
            var record = await store.GetByAccessionAsync(accession);
            return record is null ? Results.NotFound()
                : Results.Content(record.RawJson, "application/json");
        });
```

- [ ] **Step 4: Run tests** — `dotnet test`, expected PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: survey lookup by recordId and accession for the medical software"
```

---

### Task 7: Loopback gate and management API

**Files:**
- Create: `src/MedMissionBridge/Ui/LoopbackOnly.cs`
- Create: `src/MedMissionBridge/Ui/UiEndpoints.cs`
- Modify: `src/MedMissionBridge/Program.cs` (anchors `MIDDLEWARE`, `ENDPOINTS`)
- Test: `tests/MedMissionBridge.Tests/LoopbackOnlyTests.cs`, `tests/MedMissionBridge.Tests/UiApiTests.cs`

**Interfaces:**
- Consumes: `SurveyStore` queries, `StatusRules`, `BridgeOptions`.
- Produces: `static bool LoopbackOnly.IsAllowed(HttpContext ctx)` (true when remote IP is null — TestServer/in-process —, loopback, or equal to the local address); `static void UiEndpoints.Map(WebApplication app)` with:
  - `GET /api/ui/records?search=&status=` → JSON array of `{ recordId, no, firstName, lastName, city, status, date, receivedAtUtc }`
  - `GET /api/ui/records/{recordId}` → `{ recordId, status, rawJson }` (404 unknown)
  - `POST /api/ui/records/{recordId}/status` body `{ "status": "InProgress" }` → 200 / 404 / 409 invalid transition / 400 unknown value
  - `GET /api/ui/health` → `{ httpPort, mwlPort, mwlAeTitle, dbPath, serviceName }`

- [ ] **Step 1: Write the failing tests**

`tests/MedMissionBridge.Tests/LoopbackOnlyTests.cs`:

```csharp
using System.Net;
using MedMissionBridge.Ui;
using Microsoft.AspNetCore.Http;

namespace MedMissionBridge.Tests;

public class LoopbackOnlyTests
{
    private static HttpContext Ctx(string? remote)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = remote is null ? null : IPAddress.Parse(remote);
        return ctx;
    }

    [Theory]
    [InlineData(null, true)]          // in-process test server
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("192.168.1.50", false)]
    public void loopback_rule(string? remote, bool allowed) =>
        Assert.Equal(allowed, LoopbackOnly.IsAllowed(Ctx(remote)));

    [Fact]
    public void same_machine_lan_address_is_allowed()
    {
        var ctx = Ctx("192.168.1.10");
        ctx.Connection.LocalIpAddress = IPAddress.Parse("192.168.1.10");
        Assert.True(LoopbackOnly.IsAllowed(ctx));
    }
}
```

`tests/MedMissionBridge.Tests/UiApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MedMissionBridge.Tests;

public class UiApiTests
{
    private const string Payload = """{"recordId":"r-200","no":"TAB-2","patient":{"firstName":"Maria","lastName":"Santos","city":"Taytay"},"medicalHistory":{},"vitalSigns":{},"symptoms":[],"tbInfo":{},"smoking":{},"alcohol":{},"environmentalExposure":{}}""";

    private static async Task Seed(HttpClient client)
    {
        var m = new HttpRequestMessage(HttpMethod.Post, "/api/v1/surveys")
        { Content = new StringContent(Payload) };
        m.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        m.Headers.Add("X-Api-Key", BridgeAppFactory.ApiKey);
        await client.SendAsync(m);
    }

    [Fact]
    public async Task list_search_and_detail_round_trip()
    {
        using var app = new BridgeAppFactory();
        var client = app.CreateClient();
        await Seed(client);

        var list = await client.GetFromJsonAsync<JsonElement>("/api/ui/records?search=santos");
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("r-200", list[0].GetProperty("recordId").GetString());
        Assert.Equal("Received", list[0].GetProperty("status").GetString());

        var detail = await client.GetFromJsonAsync<JsonElement>("/api/ui/records/r-200");
        Assert.Equal(Payload, detail.GetProperty("rawJson").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/ui/records/none")).StatusCode);
    }

    [Fact]
    public async Task status_change_paths()
    {
        using var app = new BridgeAppFactory();
        var client = app.CreateClient();
        await Seed(client);

        var ok = await client.PostAsJsonAsync("/api/ui/records/r-200/status", new { status = "InProgress" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var invalid = await client.PostAsJsonAsync("/api/ui/records/r-200/status", new { status = "InProgress" });
        Assert.Equal(HttpStatusCode.Conflict, invalid.StatusCode); // InProgress→InProgress not allowed

        var unknownValue = await client.PostAsJsonAsync("/api/ui/records/r-200/status", new { status = "Nonsense" });
        Assert.Equal(HttpStatusCode.BadRequest, unknownValue.StatusCode);

        var missing = await client.PostAsJsonAsync("/api/ui/records/none/status", new { status = "Completed" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task health_reports_config()
    {
        using var app = new BridgeAppFactory();
        var client = app.CreateClient();
        var health = await client.GetFromJsonAsync<JsonElement>("/api/ui/health");
        Assert.Equal(11112, health.GetProperty("mwlPort").GetInt32());
        Assert.Equal("MEDMISSION", health.GetProperty("mwlAeTitle").GetString());
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter "LoopbackOnlyTests|UiApiTests"`, expected FAIL.

- [ ] **Step 3: Implement**

`src/MedMissionBridge/Ui/LoopbackOnly.cs`:

```csharp
using System.Net;

namespace MedMissionBridge.Ui;

public static class LoopbackOnly
{
    public static bool IsAllowed(HttpContext ctx)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote is null) return true; // in-process (tests) or unix socket
        if (IPAddress.IsLoopback(remote)) return true;
        return remote.Equals(ctx.Connection.LocalIpAddress); // the laptop calling its own LAN IP
    }
}
```

`src/MedMissionBridge/Ui/UiEndpoints.cs`:

```csharp
using MedMissionBridge.Data;

namespace MedMissionBridge.Ui;

public static class UiEndpoints
{
    public record StatusChange(string Status);

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/ui/records", async (string? search, string? status, SurveyStore store) =>
        {
            WorklistStatus? filter = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<WorklistStatus>(status, ignoreCase: true, out var parsed))
                    return Results.BadRequest();
                filter = parsed;
            }
            var records = await store.ListAsync(search, filter);
            return Results.Ok(records.Select(r => new
            {
                recordId = r.RecordId, no = r.No, firstName = r.FirstName, lastName = r.LastName,
                city = r.City, status = r.Status.ToString(), date = r.Date,
                receivedAtUtc = r.ReceivedAtUtc,
            }));
        });

        app.MapGet("/api/ui/records/{recordId}", async (string recordId, SurveyStore store) =>
        {
            var r = await store.GetAsync(recordId);
            return r is null ? Results.NotFound()
                : Results.Ok(new { recordId = r.RecordId, status = r.Status.ToString(), rawJson = r.RawJson });
        });

        app.MapPost("/api/ui/records/{recordId}/status", async (string recordId, StatusChange body, SurveyStore store) =>
        {
            if (!Enum.TryParse<WorklistStatus>(body.Status, ignoreCase: true, out var to))
                return Results.BadRequest();
            return await store.TryChangeStatusAsync(recordId, to) switch
            {
                StatusChangeResult.Changed => Results.Ok(),
                StatusChangeResult.NotFound => Results.NotFound(),
                _ => Results.Conflict(),
            };
        });

        app.MapGet("/api/ui/health", (BridgeOptions options) => Results.Ok(new
        {
            httpPort = options.HttpPort,
            mwlPort = options.Mwl.Port,
            mwlAeTitle = options.Mwl.AeTitle,
            dbPath = options.ResolveDbPath(),
            serviceName = options.Mdns.ResolveServiceName(),
        }));
    }
}
```

Insert after `// [ANCHOR:MIDDLEWARE]` in Program.cs:

```csharp
app.Use(async (ctx, next) =>
{
    var lanFacing = ctx.Request.Path.StartsWithSegments("/api/v1");
    if (!lanFacing && !MedMissionBridge.Ui.LoopbackOnly.IsAllowed(ctx))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});
```

Insert after the `IngestEndpoints.Map(app);` line (below `// [ANCHOR:ENDPOINTS]`):

```csharp
MedMissionBridge.Ui.UiEndpoints.Map(app);
```

- [ ] **Step 4: Run tests** — `dotnet test`, expected PASS (all).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: loopback-only management API with search, detail and status changes"
```

---

### Task 8: DICOM conversions (pure)

**Files:**
- Create: `src/MedMissionBridge/Dicom/DicomConversions.cs`
- Test: `tests/MedMissionBridge.Tests/DicomConversionsTests.cs`

**Interfaces:**
- Consumes: `SurveyRecord`, `MwlOptions`.
- Produces: `static class DicomConversions` with `string? ToDicomDate(string? iso)`, `string? ToPersonName(string? last, string? first)`, `string? ToSex(string? gender)`, `DicomDataset BuildWorklistItem(SurveyRecord r, MwlOptions m)`. Task 10's MWL service serves exactly these datasets.

- [ ] **Step 1: Write the failing tests**

```csharp
using FellowOakDicom;
using MedMissionBridge.Data;
using MedMissionBridge.Dicom;

namespace MedMissionBridge.Tests;

public class DicomConversionsTests
{
    [Theory]
    [InlineData("1980-03-04", "19800304")]
    [InlineData("2026-08-14", "20260814")]
    [InlineData("1980-03", null)]     // partial entry: attribute omitted (wire contract §6)
    [InlineData("1980-13-99", null)]  // not a calendar date
    [InlineData("", null)]
    [InlineData(null, null)]
    public void iso_to_da(string? iso, string? expected) =>
        Assert.Equal(expected, DicomConversions.ToDicomDate(iso));

    [Theory]
    [InlineData("Dela Cruz", "Juan", "Dela Cruz^Juan")]
    [InlineData("Dela Cruz", null, "Dela Cruz")]
    [InlineData(null, "Juan", "^Juan")]
    [InlineData(null, null, null)]
    public void person_name(string? last, string? first, string? expected) =>
        Assert.Equal(expected, DicomConversions.ToPersonName(last, first));

    [Theory]
    [InlineData("MALE", "M")]
    [InlineData("FEMALE", "F")]
    [InlineData("other", null)]
    [InlineData(null, null)]
    public void sex(string? gender, string? expected) =>
        Assert.Equal(expected, DicomConversions.ToSex(gender));

    [Fact]
    public void worklist_item_carries_the_mapping_table()
    {
        var r = new SurveyRecord
        {
            RecordId = "3f8b1c2e-9a4d-4c11-8e77-2b6a0d5f9c31", RawJson = "{}",
            No = "TAB-3FBB-0001", Date = "2026-08-14", FirstName = "Juan",
            LastName = "Dela Cruz", BirthDate = "1980-03-04", Gender = "MALE",
            ReceivedAtUtc = new DateTime(2026, 8, 14, 3, 0, 0, DateTimeKind.Utc),
        };
        var ds = DicomConversions.BuildWorklistItem(r, new MwlOptions());

        Assert.Equal("Dela Cruz^Juan", ds.GetSingleValue<string>(DicomTag.PatientName));
        Assert.Equal(r.RecordId, ds.GetSingleValue<string>(DicomTag.PatientID));
        Assert.Equal("19800304", ds.GetSingleValue<string>(DicomTag.PatientBirthDate));
        Assert.Equal("M", ds.GetSingleValue<string>(DicomTag.PatientSex));
        Assert.Equal("TAB-3FBB-0001", ds.GetSingleValue<string>(DicomTag.AccessionNumber));

        var sps = ds.GetSequence(DicomTag.ScheduledProcedureStepSequence).Items.Single();
        Assert.Equal("20260814", sps.GetSingleValue<string>(DicomTag.ScheduledProcedureStepStartDate));
        Assert.Equal("CR", sps.GetSingleValue<string>(DicomTag.Modality));
        Assert.Equal("MEDMISSION", sps.GetSingleValue<string>(DicomTag.ScheduledStationAETitle));
        Assert.Equal("TB Screening Chest X-Ray", sps.GetSingleValue<string>(DicomTag.ScheduledProcedureStepDescription));
    }

    [Fact]
    public void unparseable_birth_date_and_missing_name_omit_the_attributes()
    {
        var r = new SurveyRecord { RecordId = "x", RawJson = "{}", BirthDate = "1980-03" };
        var ds = DicomConversions.BuildWorklistItem(r, new MwlOptions());
        Assert.False(ds.Contains(DicomTag.PatientBirthDate));
        Assert.False(ds.Contains(DicomTag.PatientName));
        // SPS date falls back to today when the survey date is absent
        var sps = ds.GetSequence(DicomTag.ScheduledProcedureStepSequence).Items.Single();
        Assert.Equal(8, sps.GetSingleValue<string>(DicomTag.ScheduledProcedureStepStartDate).Length);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter DicomConversionsTests`, expected FAIL.

- [ ] **Step 3: Implement**

```csharp
using System.Globalization;
using FellowOakDicom;
using MedMissionBridge.Data;

namespace MedMissionBridge.Dicom;

public static class DicomConversions
{
    public static string? ToDicomDate(string? iso) =>
        DateOnly.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d)
            ? d.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : null;

    public static string? ToPersonName(string? last, string? first)
    {
        var l = (last ?? "").Trim();
        var f = (first ?? "").Trim();
        if (l.Length == 0 && f.Length == 0) return null;
        return $"{l}^{f}".TrimEnd('^');
    }

    public static string? ToSex(string? gender) => gender switch
    {
        "MALE" => "M", "FEMALE" => "F", _ => null,
    };

    public static DicomDataset BuildWorklistItem(SurveyRecord r, MwlOptions m)
    {
        var ds = new DicomDataset { { DicomTag.SpecificCharacterSet, "ISO_IR 192" } };
        AddIfPresent(ds, DicomTag.PatientName, ToPersonName(r.LastName, r.FirstName));
        ds.Add(DicomTag.PatientID, r.RecordId);
        AddIfPresent(ds, DicomTag.PatientBirthDate, ToDicomDate(r.BirthDate));
        AddIfPresent(ds, DicomTag.PatientSex, ToSex(r.Gender));
        AddIfPresent(ds, DicomTag.AccessionNumber, r.No);

        var sps = new DicomDataset
        {
            { DicomTag.ScheduledProcedureStepStartDate,
              ToDicomDate(r.Date) ?? DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) },
            { DicomTag.ScheduledProcedureStepStartTime,
              r.ReceivedAtUtc.ToLocalTime().ToString("HHmmss", CultureInfo.InvariantCulture) },
            { DicomTag.Modality, m.Modality },
            { DicomTag.ScheduledStationAETitle, m.StationAeTitle },
            { DicomTag.ScheduledProcedureStepDescription, m.ProcedureDescription },
        };
        ds.Add(new DicomSequence(DicomTag.ScheduledProcedureStepSequence, sps));
        return ds;
    }

    private static void AddIfPresent(DicomDataset ds, DicomTag tag, string? value)
    {
        if (value is { Length: > 0 }) ds.Add(tag, value);
    }
}
```

- [ ] **Step 4: Run tests** — `dotnet test --filter DicomConversionsTests`, expected PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: survey-to-MWL DICOM attribute conversions"
```

---

### Task 9: Worklist C-FIND matcher (pure)

**Files:**
- Create: `src/MedMissionBridge/Dicom/WorklistMatcher.cs`
- Test: `tests/MedMissionBridge.Tests/WorklistMatcherTests.cs`

**Interfaces:**
- Consumes: `DicomDataset` items built by `DicomConversions.BuildWorklistItem`.
- Produces: `static bool WorklistMatcher.Matches(DicomDataset query, DicomDataset item)` — matching on Patient ID (exact), Patient Name (`*`/`?` wildcards, case-insensitive), and within the query's SPS item: Modality (exact) and SPS start date (single value `yyyyMMdd` or ranges `a-b`, `a-`, `-b`). Empty/absent query keys match everything.

- [ ] **Step 1: Write the failing tests**

```csharp
using FellowOakDicom;
using MedMissionBridge.Data;
using MedMissionBridge.Dicom;

namespace MedMissionBridge.Tests;

public class WorklistMatcherTests
{
    private static DicomDataset Item() => DicomConversions.BuildWorklistItem(
        new SurveyRecord
        {
            RecordId = "r1", RawJson = "{}", No = "TAB-1", Date = "2026-08-14",
            FirstName = "Juan", LastName = "Dela Cruz",
        }, new MwlOptions());

    private static DicomDataset Query(Action<DicomDataset, DicomDataset>? fill = null)
    {
        var q = new DicomDataset();
        var sps = new DicomDataset();
        fill?.Invoke(q, sps);
        if (sps.Any()) q.Add(new DicomSequence(DicomTag.ScheduledProcedureStepSequence, sps));
        return q;
    }

    [Fact]
    public void empty_query_matches() => Assert.True(WorklistMatcher.Matches(Query(), Item()));

    [Theory]
    [InlineData("r1", true)]
    [InlineData("other", false)]
    public void patient_id_exact(string id, bool expected) =>
        Assert.Equal(expected, WorklistMatcher.Matches(
            Query((q, _) => q.Add(DicomTag.PatientID, id)), Item()));

    [Theory]
    [InlineData("Dela Cruz^Juan", true)]
    [InlineData("DELA*", true)]
    [InlineData("*juan*", true)]
    [InlineData("?ela*", true)]
    [InlineData("Santos*", false)]
    public void patient_name_wildcards(string pattern, bool expected) =>
        Assert.Equal(expected, WorklistMatcher.Matches(
            Query((q, _) => q.Add(DicomTag.PatientName, pattern)), Item()));

    [Theory]
    [InlineData("CR", true)]
    [InlineData("DX", false)]
    public void modality_exact(string modality, bool expected) =>
        Assert.Equal(expected, WorklistMatcher.Matches(
            Query((_, sps) => sps.Add(DicomTag.Modality, modality)), Item()));

    [Theory]
    [InlineData("20260814", true)]
    [InlineData("20260813", false)]
    [InlineData("20260801-20260820", true)]
    [InlineData("20260815-", false)]
    [InlineData("-20260815", true)]
    public void sps_date_single_and_ranges(string date, bool expected) =>
        Assert.Equal(expected, WorklistMatcher.Matches(
            Query((_, sps) => sps.Add(DicomTag.ScheduledProcedureStepStartDate, date)), Item()));
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter WorklistMatcherTests`, expected FAIL.

- [ ] **Step 3: Implement**

```csharp
using System.Text.RegularExpressions;
using FellowOakDicom;

namespace MedMissionBridge.Dicom;

public static class WorklistMatcher
{
    public static bool Matches(DicomDataset query, DicomDataset item)
    {
        var qId = Get(query, DicomTag.PatientID);
        if (qId.Length > 0 && qId != Get(item, DicomTag.PatientID)) return false;

        var qName = Get(query, DicomTag.PatientName);
        if (qName.Length > 0 && !WildcardMatches(qName, Get(item, DicomTag.PatientName))) return false;

        if (query.TryGetSequence(DicomTag.ScheduledProcedureStepSequence, out var qSeq)
            && qSeq.Items.Count > 0)
        {
            var q = qSeq.Items[0];
            var i = item.GetSequence(DicomTag.ScheduledProcedureStepSequence).Items[0];

            var qModality = Get(q, DicomTag.Modality);
            if (qModality.Length > 0 && qModality != Get(i, DicomTag.Modality)) return false;

            var qDate = Get(q, DicomTag.ScheduledProcedureStepStartDate);
            if (qDate.Length > 0
                && !DateMatches(qDate, Get(i, DicomTag.ScheduledProcedureStepStartDate)))
                return false;
        }
        return true;
    }

    private static string Get(DicomDataset ds, DicomTag tag) =>
        ds.GetSingleValueOrDefault(tag, string.Empty);

    private static bool WildcardMatches(string pattern, string value)
    {
        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase);
    }

    private static bool DateMatches(string queryDate, string itemDate)
    {
        if (itemDate.Length == 0) return false;
        var dash = queryDate.IndexOf('-');
        if (dash < 0) return itemDate == queryDate;
        var from = queryDate[..dash];
        var to = queryDate[(dash + 1)..];
        // yyyyMMdd strings compare correctly as ordinals
        if (from.Length > 0 && string.CompareOrdinal(itemDate, from) < 0) return false;
        if (to.Length > 0 && string.CompareOrdinal(itemDate, to) > 0) return false;
        return true;
    }
}
```

- [ ] **Step 4: Run tests** — `dotnet test --filter WorklistMatcherTests`, expected PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: MWL C-FIND matching for patient, modality and date keys"
```

---

### Task 10: MWL SCP server and host wiring

**Files:**
- Create: `src/MedMissionBridge/Dicom/MwlService.cs`
- Create: `src/MedMissionBridge/Dicom/MwlServer.cs`
- Modify: `src/MedMissionBridge/Program.cs` (anchor `SERVERS`)
- Test: `tests/MedMissionBridge.Tests/MwlRoundTripTests.cs`

**Interfaces:**
- Consumes: `WorklistMatcher`, `DicomConversions`, `SurveyStore.GetScheduledAsync`.
- Produces: `MwlService : DicomService, IDicomServiceProvider, IDicomCEchoProvider, IDicomCFindProvider` with `static Func<Task<IReadOnlyList<DicomDataset>>>? WorklistSource` (set by the host before the server starts; tests set a fake); `sealed class MwlServer(int port) : IDisposable` wrapping `DicomServerFactory.Create<MwlService>`; `static class DicomSetup { static void EnsureInitialized() }` guarding the one-time fo-dicom setup.

fo-dicom 5.1 API notes for the implementer: the service constructor signature is `(INetworkStream stream, Encoding fallbackEncoding, Microsoft.Extensions.Logging.ILogger log, DicomServiceDependencies dependencies)`; `IDicomCFindProvider.OnCFindRequestAsync` returns `IAsyncEnumerable<DicomCFindResponse>`. If a signature differs on the restored version, match the interface definitions in the package — the shape below is the intent.

- [ ] **Step 1: Write the failing test**

```csharp
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
        using var server = new MwlServer(port);

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
        using var server = new MwlServer(port);

        var client = DicomClientFactory.Create("127.0.0.1", port, false, "TESTSCU", "MEDMISSION");
        DicomStatus? status = null;
        var echo = new DicomCEchoRequest();
        echo.OnResponseReceived += (_, resp) => status = resp.Status;
        await client.AddRequestAsync(echo);
        await client.SendAsync();

        Assert.Equal(DicomStatus.Success, status);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter MwlRoundTripTests`, expected FAIL (types missing).

- [ ] **Step 3: Implement**

`src/MedMissionBridge/Dicom/MwlService.cs`:

```csharp
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
```

`src/MedMissionBridge/Dicom/MwlServer.cs`:

```csharp
using FellowOakDicom.Network;

namespace MedMissionBridge.Dicom;

public sealed class MwlServer : IDisposable
{
    private readonly IDicomServer _server;
    public MwlServer(int port) => _server = DicomServerFactory.Create<MwlService>(port);
    public void Dispose() => _server.Dispose();
}
```

Insert after `// [ANCHOR:SERVERS]` in Program.cs:

```csharp
if (!isTesting)
{
    MedMissionBridge.Dicom.DicomSetup.EnsureInitialized();
    var store = app.Services.GetRequiredService<MedMissionBridge.Data.SurveyStore>();
    MedMissionBridge.Dicom.MwlService.WorklistSource = async () =>
    {
        var scheduled = await store.GetScheduledAsync();
        return scheduled
            .Select(r => MedMissionBridge.Dicom.DicomConversions.BuildWorklistItem(r, bridge.Mwl))
            .ToList();
    };
    var mwlServer = new MedMissionBridge.Dicom.MwlServer(bridge.Mwl.Port);
    app.Lifetime.ApplicationStopping.Register(mwlServer.Dispose);
    Log.Information("MWL SCP listening on port {Port}, AE {Ae}", bridge.Mwl.Port, bridge.Mwl.AeTitle);
}
```

- [ ] **Step 4: Run tests** — `dotnet test`, expected PASS (all).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: DICOM MWL SCP serving the scheduled worklist"
```

---

### Task 11: mDNS advertiser

**Files:**
- Create: `src/MedMissionBridge/Mdns/MdnsAdvertiser.cs`
- Modify: `src/MedMissionBridge/Program.cs` (anchor `SERVERS`, after the MWL block)
- Test: `tests/MedMissionBridge.Tests/MdnsAdvertiserTests.cs` (construction/disposal only — real multicast behavior is verified manually per spec §11)

- [ ] **Step 1: Write the smoke test**

```csharp
using MedMissionBridge.Mdns;

namespace MedMissionBridge.Tests;

public class MdnsAdvertiserTests
{
    [Fact]
    public void constructs_and_disposes_without_throwing()
    {
        using var advertiser = new MdnsAdvertiser("TEST-LAPTOP", 18080);
    }
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter MdnsAdvertiserTests`, expected FAIL.

- [ ] **Step 3: Implement**

```csharp
using Makaretu.Dns;

namespace MedMissionBridge.Mdns;

/// <summary>
/// Advertises `_medmission._tcp` so tablets list this laptop automatically.
/// The service type must match the tablet's NsdDiscoveryService exactly.
/// </summary>
public sealed class MdnsAdvertiser : IDisposable
{
    private readonly ServiceDiscovery _sd = new();

    public MdnsAdvertiser(string instanceName, int port) =>
        _sd.Advertise(new ServiceProfile(instanceName, "_medmission._tcp", (ushort)port));

    public void Dispose() => _sd.Dispose();
}
```

If `Makaretu.Dns.Multicast`'s API differs on the restored version (`ServiceDiscovery`/`ServiceProfile` live in the `Makaretu.Dns` namespace), adapt to the package's README shape and note it in your report.

Insert into Program.cs directly after the MWL block inside the same `if (!isTesting)` (below the `Log.Information` MWL line):

```csharp
    var advertiser = new MedMissionBridge.Mdns.MdnsAdvertiser(
        bridge.Mdns.ResolveServiceName(), bridge.HttpPort);
    app.Lifetime.ApplicationStopping.Register(advertiser.Dispose);
    Log.Information("mDNS advertising {Name} on _medmission._tcp:{Port}",
        bridge.Mdns.ResolveServiceName(), bridge.HttpPort);
```

- [ ] **Step 4: Run tests** — `dotnet test`, expected PASS.

- [ ] **Step 5: Manual verification (record the result in your report)**

Run `dotnet run --project src/MedMissionBridge` and, from another shell:
`dns-sd -B _medmission._tcp` (if Bonjour tooling is present) or check with a tablet on the same network. If neither is available in this session, state so — the spec (§11) assigns final mDNS verification to the live laptop+tablet check.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat: advertise _medmission._tcp so tablets discover the bridge"
```

---

### Task 12: Management web UI

**Files:**
- Create: `src/MedMissionBridge/wwwroot/index.html`
- Create: `src/MedMissionBridge/wwwroot/app.js`
- Create: `src/MedMissionBridge/wwwroot/style.css`
- Modify: `src/MedMissionBridge/Program.cs` (anchor `STATIC`)
- Test: append one integration test to `tests/MedMissionBridge.Tests/UiApiTests.cs`

No JS framework, no build step. The detail view renders the raw payload generically (section per top-level key) so survey schema changes never break the UI.

- [ ] **Step 1: Write the failing test** (append to `UiApiTests.cs`)

```csharp
    [Fact]
    public async Task root_serves_the_management_page()
    {
        using var app = new BridgeAppFactory();
        var client = app.CreateClient();
        var html = await client.GetStringAsync("/");
        Assert.Contains("MedMission Bridge", html);
    }
```

- [ ] **Step 2: Run to verify failure** — `dotnet test --filter UiApiTests`, expected FAIL (404).

- [ ] **Step 3: Write the UI files**

`src/MedMissionBridge/wwwroot/index.html`:

```html
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>MedMission Bridge</title>
<link rel="stylesheet" href="style.css">
</head>
<body>
<header>
  <h1>MedMission Bridge</h1>
  <div id="health" class="health">loading…</div>
</header>
<main>
  <section class="controls">
    <input id="search" type="search" placeholder="Search name / No. / city">
    <select id="statusFilter">
      <option value="">All statuses</option>
      <option>Received</option>
      <option>InProgress</option>
      <option>Completed</option>
      <option>Cancelled</option>
    </select>
    <button id="refresh">Refresh</button>
  </section>
  <section class="split">
    <table id="records">
      <thead>
        <tr><th>No.</th><th>Name</th><th>City</th><th>Date</th><th>Status</th><th>Received (UTC)</th></tr>
      </thead>
      <tbody></tbody>
    </table>
    <aside id="detail" class="detail"><p class="hint">Select a record.</p></aside>
  </section>
</main>
<script src="app.js"></script>
</body>
</html>
```

`src/MedMissionBridge/wwwroot/app.js`:

```javascript
const transitions = {
  Received: ["InProgress", "Completed", "Cancelled"],
  InProgress: ["Completed", "Cancelled", "Received"],
  Completed: ["InProgress"],
  Cancelled: ["Received"],
};

async function loadHealth() {
  const h = await (await fetch("/api/ui/health")).json();
  document.getElementById("health").textContent =
    `HTTP :${h.httpPort} · MWL :${h.mwlPort} (${h.mwlAeTitle}) · mDNS ${h.serviceName} · ${h.dbPath}`;
}

async function loadList() {
  const search = document.getElementById("search").value.trim();
  const status = document.getElementById("statusFilter").value;
  const params = new URLSearchParams();
  if (search) params.set("search", search);
  if (status) params.set("status", status);
  const rows = await (await fetch(`/api/ui/records?${params}`)).json();
  const tbody = document.querySelector("#records tbody");
  tbody.replaceChildren(...rows.map((r) => {
    const tr = document.createElement("tr");
    tr.dataset.recordId = r.recordId;
    for (const v of [r.no, [r.lastName, r.firstName].filter(Boolean).join(", "),
                     r.city, r.date, r.status, r.receivedAtUtc?.replace("T", " ").slice(0, 19)]) {
      const td = document.createElement("td");
      td.textContent = v ?? "";
      tr.appendChild(td);
    }
    tr.addEventListener("click", () => showDetail(r.recordId));
    return tr;
  }));
}

function renderValue(v) {
  if (Array.isArray(v)) return v.join(", ");
  if (v && typeof v === "object") return "";
  return String(v);
}

async function showDetail(recordId) {
  const detail = await (await fetch(`/api/ui/records/${recordId}`)).json();
  const payload = JSON.parse(detail.rawJson);
  const box = document.getElementById("detail");
  box.replaceChildren();

  const title = document.createElement("h2");
  title.textContent = `${payload.no ?? recordId} — ${detail.status}`;
  box.appendChild(title);

  const actions = document.createElement("div");
  actions.className = "actions";
  for (const to of transitions[detail.status] ?? []) {
    const b = document.createElement("button");
    b.textContent = `→ ${to}`;
    b.addEventListener("click", async () => {
      const resp = await fetch(`/api/ui/records/${recordId}/status`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ status: to }),
      });
      if (!resp.ok) alert(`Status change failed (${resp.status})`);
      await loadList();
      await showDetail(recordId);
    });
    actions.appendChild(b);
  }
  box.appendChild(actions);

  for (const [section, value] of Object.entries(payload)) {
    const h = document.createElement("h3");
    h.textContent = section;
    box.appendChild(h);
    const table = document.createElement("table");
    table.className = "kv";
    const entries = value && typeof value === "object" && !Array.isArray(value)
      ? Object.entries(value) : [[section, value]];
    for (const [k, v] of entries) {
      const tr = document.createElement("tr");
      const kt = document.createElement("td"); kt.textContent = k;
      const vt = document.createElement("td"); vt.textContent = renderValue(v);
      tr.append(kt, vt);
      table.appendChild(tr);
    }
    box.appendChild(table);
  }
}

document.getElementById("refresh").addEventListener("click", loadList);
document.getElementById("search").addEventListener("input", () => loadList());
document.getElementById("statusFilter").addEventListener("change", loadList);
loadHealth();
loadList();
setInterval(loadList, 5000);
```

`src/MedMissionBridge/wwwroot/style.css`:

```css
* { box-sizing: border-box; }
body { margin: 0; font-family: "Segoe UI", system-ui, sans-serif; background: #f4f5f2; color: #1c2b2a; }
header { display: flex; align-items: baseline; gap: 16px; padding: 12px 20px; background: #0d4f4a; color: #fff; }
header h1 { margin: 0; font-size: 1.2rem; }
.health { font-size: 0.8rem; opacity: 0.85; }
main { padding: 16px 20px; }
.controls { display: flex; gap: 8px; margin-bottom: 12px; }
.controls input[type=search] { flex: 1; max-width: 360px; padding: 6px 10px; }
.split { display: grid; grid-template-columns: 1.4fr 1fr; gap: 16px; align-items: start; }
table { border-collapse: collapse; width: 100%; background: #fff; }
th, td { text-align: left; padding: 6px 10px; border-bottom: 1px solid #e2e5e0; font-size: 0.9rem; }
#records tbody tr { cursor: pointer; }
#records tbody tr:hover { background: #eef4f3; }
.detail { background: #fff; padding: 12px 16px; border: 1px solid #e2e5e0; max-height: 80vh; overflow-y: auto; }
.detail h2 { margin-top: 0; font-size: 1rem; }
.detail h3 { margin: 14px 0 4px; font-size: 0.85rem; text-transform: capitalize; color: #0d4f4a; }
.kv td:first-child { color: #5a6c6a; width: 45%; }
.actions { display: flex; gap: 8px; flex-wrap: wrap; }
.actions button { padding: 6px 12px; cursor: pointer; }
.hint { color: #8a9694; }
```

- [ ] **Step 4: Wire Program.cs**

Insert after `// [ANCHOR:STATIC]`:

```csharp
app.UseDefaultFiles();
app.UseStaticFiles();
```

(The loopback middleware at `[ANCHOR:MIDDLEWARE]` runs before static files, so the UI is unreachable from the LAN.)

- [ ] **Step 5: Run tests** — `dotnet test`, expected PASS (all).

- [ ] **Step 6: Manual check**

`dotnet run --project src/MedMissionBridge`, open `http://localhost:18080/`, POST a sample payload (Task 13 has the curl command) and confirm: list shows it, detail renders sections, status buttons work. Screenshot or describe in your report.

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "feat: loopback-only management web UI"
```

---

### Task 13: End-to-end verification and README

**Files:**
- Create: `README.md`
- Test: full suite + manual end-to-end

- [ ] **Step 1: Full suite**

Run: `dotnet test`
Expected: all green, no warnings from files this plan created.

- [ ] **Step 2: End-to-end smoke (manual, record output in your report)**

```bash
dotnet run --project src/MedMissionBridge &
sleep 5
curl -s -o /dev/null -w "%{http_code}\n" -X POST http://localhost:18080/api/v1/surveys \
  -H "X-Api-Key: changeme-dev-key" -H "Content-Type: application/json" \
  -d '{"recordId":"e2e-1","no":"TAB-E2E-0001","date":"2026-08-15","patient":{"firstName":"Juan","lastName":"Dela Cruz","birthDate":"1980-03-04","gender":"MALE","region":"NATIONAL CAPITAL REGION (NCR)","city":"City Of Manila","barangay":"Ermita","zip":"1000"},"medicalHistory":{},"vitalSigns":{},"symptoms":[],"tbInfo":{},"smoking":{},"alcohol":{},"environmentalExposure":{}}'
# expect 200
curl -s http://localhost:18080/api/ui/records | head -c 400
# expect a JSON array containing e2e-1
curl -s -H "X-Api-Key: changeme-dev-key" "http://localhost:18080/api/v1/surveys?accession=TAB-E2E-0001" | head -c 200
# expect the raw payload back
```

Then stop the process. (MWL C-FIND against the running instance is already covered by `MwlRoundTripTests`; the live modality check happens on the deployment laptop.)

- [ ] **Step 3: Write `README.md`**

```markdown
# MedMission Bridge

Laptop-side receiver for the MedMission tablet survey app: HTTP ingest,
management worklist UI, and DICOM Modality Worklist (MWL) SCP for the
co-located medical software.

## Run

    dotnet run --project src/MedMissionBridge

- Management UI: http://localhost:18080/ (loopback-only)
- Tablet ingest: POST /api/v1/surveys with `X-Api-Key` (LAN)
- Survey lookup: GET /api/v1/surveys/{recordId} or ?accession= (LAN, keyed)
- MWL SCP: port 11112, AE `MEDMISSION` (C-ECHO supported)
- mDNS: advertises `_medmission._tcp` for tablet discovery

Configuration: `src/MedMissionBridge/appsettings.json`, section `Bridge`
(API key, ports, AE title, modality, DB path, mDNS name). Data and logs:
`%ProgramData%\MedMissionBridge\`.

## Contracts

- Payload and semantics: tablet repo `docs/reference/wire-contract.md`.
- Design: `docs/superpowers/specs/2026-08-15-medmission-bridge-design.md`.

## Test

    dotnet test
```

- [ ] **Step 4: Confirm clean tree and commit**

```bash
git status --short   # expect only README.md before the add
git add -A && git commit -m "docs: README with run, contract and test instructions"
```

---

## Self-review notes (for whoever executes this plan)

- **Spec coverage:** §3 architecture → Tasks 1, 5, 7, 10, 11, 12; §4 ingest + mDNS → Tasks 2, 5, 11; §5 storage/lifecycle → Tasks 3, 4; §6 UI/loopback → Tasks 7, 12; §7 MWL mapping/matching/visibility → Tasks 8, 9, 10; §8 REST handoff → Task 6 (private tag deliberately absent per spec); §9 reliability/logging → Tasks 1, 5 (500-on-store-failure comes free from ASP.NET's exception handling: an unhandled store exception surfaces as 500, which the tablet retries); §10 security → Tasks 5, 7 (TLS deliberately absent); §11 testing → every task; §12 exclusions respected throughout.
- The `no`-collision behavior (`GetByAccessionAsync` latest-wins) matches spec §8 exactly.
- Library-version pins may drift from what NuGet can restore; implementers adjust to nearest patch and report. fo-dicom/Makaretu API-shape caveats are called out inside Tasks 10 and 11 where they apply.
- Status transition undo paths (Task 4) extend spec §5's minimum; the rationale (field mis-tap recovery) is documented at the transition table, and MWL visibility (§7) follows status automatically either way.
