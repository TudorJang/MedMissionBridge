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
