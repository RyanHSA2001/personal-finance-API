namespace PersonalControl.Api.Cards;

public enum CardDeleteTxResult
{
    Deleted,
    NotFound,
    InPaidInvoice,
}

public enum PayInvoiceResult
{
    Paid,
    CardNotFound,
    AlreadyPaid,
    EmptyInvoice,
}

public interface ICardStore
{
    Task<IReadOnlyList<CardDto>> ListCardsAsync(Guid userId, bool includeArchived = false, CancellationToken ct = default);
    Task<CardDto?> GetCardAsync(Guid userId, Guid cardId, CancellationToken ct = default);
    Task<Guid> AddCardAsync(Guid userId, CardRequest request, CancellationToken ct = default);
    Task<bool> UpdateCardAsync(Guid userId, Guid cardId, CardRequest request, CancellationToken ct = default);
    Task<bool> DeleteCardAsync(Guid userId, Guid cardId, CancellationToken ct = default);

    Task<CardInvoiceDto?> GetInvoiceAsync(Guid userId, Guid cardId, string billingMonth, CancellationToken ct = default);

    Task<CardTransactionCreatedDto> AddCardTransactionAsync(
        Guid userId,
        Guid cardId,
        CardTransactionRequest request,
        string firstBillingMonth,
        CancellationToken ct = default);

    Task<CardDeleteTxResult> DeleteCardTransactionAsync(
        Guid userId,
        Guid cardId,
        Guid transactionId,
        bool deleteGroup,
        CancellationToken ct = default);

    Task<PayInvoiceResult> PayInvoiceAsync(
        Guid userId,
        Guid cardId,
        string billingMonth,
        PayInvoiceRequest request,
        CancellationToken ct = default);

    Task<bool> UnpayInvoiceAsync(
        Guid userId,
        Guid cardId,
        string billingMonth,
        CancellationToken ct = default);
}
