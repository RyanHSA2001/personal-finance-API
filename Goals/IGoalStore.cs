namespace PersonalControl.Api.Goals;

public interface IGoalStore
{
    Task<GoalsOverviewDto> GetOverviewAsync(Guid userId, string month, DateOnly today, CancellationToken ct = default);

    Task<Guid> AddGoalAsync(Guid userId, GoalRequest request, CancellationToken ct = default);

    Task<long?> ContributeAsync(Guid userId, Guid goalId, long deltaCents, CancellationToken ct = default);

    Task<bool> DeleteGoalAsync(Guid userId, Guid goalId, CancellationToken ct = default);

    Task UpsertDailyBudgetAsync(Guid userId, DailyBudgetRequest request, CancellationToken ct = default);

    Task DeleteDailyBudgetAsync(Guid userId, CancellationToken ct = default);
}
