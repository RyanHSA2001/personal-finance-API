namespace PersonalControl.Api.Finance;

// Valores monetários são sempre em centavos (long). Ex.: R$ 12,50 = 1250.

public record AccountRequest(string Name, string Kind, long InitialBalanceCents);

public record AccountDto(Guid Id, string Name, string Kind, long InitialBalanceCents, long BalanceCents);

public record CategoryRequest(string Name, string Kind, string? Color);

public record CategoryDto(Guid Id, string Name, string Kind, string Color);

public record TransactionRequest(
    string Type,
    long AmountCents,
    string? Description,
    string OccurredOn,
    Guid AccountId,
    Guid? CategoryId,
    Guid? TransferAccountId);

public record TransactionDto(
    Guid Id,
    string Type,
    long AmountCents,
    string Description,
    string OccurredOn,
    Guid AccountId,
    string AccountName,
    Guid? CategoryId,
    string? CategoryName,
    string? CategoryColor,
    Guid? TransferAccountId,
    string? TransferAccountName);

public record BudgetRequest(Guid CategoryId, long MonthlyLimitCents);

// SpentCents é o gasto da categoria no mês consultado.
public record BudgetDto(
    Guid Id,
    Guid CategoryId,
    string CategoryName,
    string CategoryColor,
    long MonthlyLimitCents,
    long SpentCents);

public record CategorySpendDto(Guid? CategoryId, string Name, string Color, long AmountCents);

public record MonthTrendDto(string Month, long IncomeCents, long ExpenseCents);

public record DashboardDto(
    string Month,
    long TotalBalanceCents,
    long MonthIncomeCents,
    long MonthExpenseCents,
    long MonthNetCents,
    IReadOnlyList<AccountDto> Accounts,
    IReadOnlyList<CategorySpendDto> ExpenseByCategory,
    IReadOnlyList<MonthTrendDto> MonthlyTrend,
    IReadOnlyList<BudgetDto> Budgets,
    IReadOnlyList<TransactionDto> RecentTransactions);
