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
