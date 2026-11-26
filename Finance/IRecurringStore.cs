namespace PersonalControl.Api.Finance;

public interface IRecurringStore
{
    Task<IReadOnlyList<RecurringDto>> ListAsync(Guid userId, string month, CancellationToken ct = default);

    Task<Guid> AddAsync(Guid userId, RecurringRequest request, CancellationToken ct = default);

    Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken ct = default);

    Task<RecurringApplyResultDto> ApplyMonthAsync(Guid userId, string month, Guid? onlyRecurringId = null, CancellationToken ct = default);

    // Retorna sugestões de categoria baseadas no histórico do próprio usuário (descrição normalizada -> categoryId).
    Task<Dictionary<string, Guid>> GetCategorySuggestionsAsync(Guid userId, string type, CancellationToken ct = default);
}
