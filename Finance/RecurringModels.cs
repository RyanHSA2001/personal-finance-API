namespace PersonalControl.Api.Finance;

public record RecurringRequest(
    string Name,
    string Type,
    long AmountCents,
    int DayOfMonth,
    Guid AccountId,
    Guid? CategoryId);

public record RecurringDto(
    Guid Id,
    string Name,
    string Type,
    long AmountCents,
    int DayOfMonth,
    Guid AccountId,
    string AccountName,
    Guid? CategoryId,
    string? CategoryName,
    string? CategoryColor,
    bool AppliedInMonth);

public record RecurringApplyResultDto(int Applied, int Skipped);
