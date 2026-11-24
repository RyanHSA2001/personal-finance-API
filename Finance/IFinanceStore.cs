namespace PersonalControl.Api.Finance;

// Contrato de acesso aos dados financeiros.
public interface IFinanceStore
{
    Task<IReadOnlyList<AccountDto>> ListAccountsAsync(Guid userId, CancellationToken ct = default);
    Task<Guid> AddAccountAsync(Guid userId, AccountRequest request, CancellationToken ct = default);
    Task<bool> DeleteAccountAsync(Guid userId, Guid id, CancellationToken ct = default);
    Task<bool> AccountBelongsToAsync(Guid userId, Guid id, CancellationToken ct = default);

    Task EnsureDefaultCategoriesAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<CategoryDto>> ListCategoriesAsync(Guid userId, CancellationToken ct = default);
    Task<Guid> AddCategoryAsync(Guid userId, CategoryRequest request, CancellationToken ct = default);
    Task<bool> DeleteCategoryAsync(Guid userId, Guid id, CancellationToken ct = default);
    Task<string?> CategoryKindAsync(Guid userId, Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<TransactionDto>> ListTransactionsAsync(
        Guid userId,
        string? month = null,
        Guid? accountId = null,
        Guid? categoryId = null,
        int limit = 200,
        CancellationToken ct = default);
    Task<Guid> AddTransactionAsync(Guid userId, TransactionRequest request, CancellationToken ct = default);
    Task<bool> DeleteTransactionAsync(Guid userId, Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<BudgetDto>> ListBudgetsAsync(Guid userId, string month, CancellationToken ct = default);
    Task UpsertBudgetAsync(Guid userId, BudgetRequest request, CancellationToken ct = default);
    Task<bool> DeleteBudgetAsync(Guid userId, Guid id, CancellationToken ct = default);

    Task<DashboardDto> GetDashboardAsync(Guid userId, string month, CancellationToken ct = default);
}
