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
