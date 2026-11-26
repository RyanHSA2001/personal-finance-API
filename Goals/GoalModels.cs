namespace PersonalControl.Api.Goals;

public record GoalDto(
    Guid Id,
    string Name,
    string Kind,
    long TargetCents,
    long CurrentCents,
    long EffectiveCents,
    int ProgressPercent,
    string? Deadline,
    string Color);

public record GoalRequest(
    string Name,
    string Kind,
    long TargetCents,
    long CurrentCents,
    string? Deadline,
    string? Color);

public record GoalContributeRequest(long DeltaCents);

public record DailyBudgetRequest(long DailyLimitCents, bool IncludeCardPurchases);

public record DailySpendDayDto(
    string Date,
    int DayOfMonth,
    long SpentCents,
    string Status);

public record DailyBudgetSummaryDto(
    long? DailyLimitCents,
    bool IncludeCardPurchases,
    long TodaySpentCents,
    int CurrentStreak,
    int BestStreakInMonth,
    int DaysWithinLimit,
    int DaysExceeded,
    int DaysElapsed,
    IReadOnlyList<DailySpendDayDto> Days);

public record GoalsOverviewDto(
    string Month,
    string Today,
    long MonthIncomeCents,
    long MonthExpenseCents,
    long MonthNetCents,
    IReadOnlyList<GoalDto> Goals,
    DailyBudgetSummaryDto DailyBudget);
