namespace PersonalControl.Api.Finance;

// Valores monetários são sempre em centavos (long). Ex.: R$ 12,50 = 1250.

public record AccountRequest(string Name, string Kind, long InitialBalanceCents);

public record AccountDto(
    Guid Id,
    string Name,
    string Kind,
    long InitialBalanceCents,
    long BalanceCents,
    long ProjectedBalanceCents);

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
    long ProjectedBalanceCents,
    long MonthIncomeCents,
    long MonthAccountExpenseCents,
    long MonthExpenseCents,
    long MonthNetCents,
    long MonthCardInvoicesCents,
    long OpenCardInvoicesCents,
    IReadOnlyList<AccountDto> Accounts,
    IReadOnlyList<CategorySpendDto> ExpenseByCategory,
    IReadOnlyList<MonthTrendDto> MonthlyTrend,
    IReadOnlyList<BudgetDto> Budgets,
    IReadOnlyList<TransactionDto> RecentTransactions);

// ---------- Importação OFX ----------

// Lançamento lido do OFX. AmountCents é sempre positivo; Type indica entrada (income) ou saída (expense).
// AlreadyImported fica true quando o ExternalId já existe na conta escolhida.
public record OfxPreviewTransactionDto(
    string ExternalId,
    string Type,
    long AmountCents,
    string Description,
    string OccurredOn,
    string OfxType,
    bool AlreadyImported,
    Guid? SuggestedCategoryId = null);

public record OfxPreviewDto(
    string? BankId,
    string? AccountNumber,
    string? Currency,
    string? StartDate,
    string? EndDate,
    long? LedgerBalanceCents,
    IReadOnlyList<OfxPreviewTransactionDto> Transactions);

public record OfxImportItem(
    string ExternalId,
    string Type,
    long AmountCents,
    string? Description,
    string OccurredOn,
    Guid? CategoryId);

public record OfxImportRequest(Guid AccountId, IReadOnlyList<OfxImportItem> Transactions);

public record OfxImportResultDto(int Imported, int Skipped);
