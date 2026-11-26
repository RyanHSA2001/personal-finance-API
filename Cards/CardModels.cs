namespace PersonalControl.Api.Cards;

// Modelos do módulo de cartões.
// Segurança: nunca aceitamos nem armazenamos número completo (PAN), CVV/CVC ou senha.
// Apenas metadados não sensíveis (apelido, bandeira, emissor e opcionalmente os 4 últimos dígitos).

public record CardRequest(
    string Name,
    string Kind,
    string Brand,
    string? Issuer,
    string? Last4,
    string? Color,
    long? CreditLimitCents,
    int? ClosingDay,
    int? DueDay,
    Guid? DefaultPaymentAccountId,
    Guid? LinkedAccountId,
    bool IsArchived = false);

public record CardDto(
    Guid Id,
    string Name,
    string Kind,
    string Brand,
    string Issuer,
    string? Last4,
    string Color,
    long? CreditLimitCents,
    long UsedLimitCents,
    long AvailableLimitCents,
    int? ClosingDay,
    int? DueDay,
    Guid? DefaultPaymentAccountId,
    string? DefaultPaymentAccountName,
    Guid? LinkedAccountId,
    string? LinkedAccountName,
    long? LinkedAccountBalanceCents,
    string? CurrentInvoiceMonth,
    long CurrentInvoiceCents,
    string? CurrentInvoiceStatus,
    bool IsArchived);

public record CardTransactionRequest(
    string Type,
    long TotalAmountCents,
    string? Description,
    string OccurredOn,
    Guid? CategoryId,
    int Installments = 1,
    string? FirstBillingMonth = null);

public record CardTransactionDto(
    Guid Id,
    Guid CardId,
    string Type,
    long AmountCents,
    string Description,
    string OccurredOn,
    string BillingMonth,
    Guid? CategoryId,
    string? CategoryName,
    string? CategoryColor,
    Guid? InstallmentGroupId,
    int InstallmentNumber,
    int InstallmentTotal);

public record CardInvoicePaymentDto(
    Guid Id,
    long AmountCents,
    string PaidOn,
    Guid? AccountId,
    string? AccountName);

public record CardInvoiceDto(
    Guid CardId,
    string CardName,
    string BillingMonth,
    string ClosingDate,
    string DueDate,
    string Status,
    long TotalCents,
    long? PaidAmountCents,
    long RemainingCents,
    string? PaidOn,
    Guid? PaymentAccountId,
    string? PaymentAccountName,
    IReadOnlyList<CardInvoicePaymentDto> Payments,
    IReadOnlyList<CardTransactionDto> Items);

public record PayInvoiceRequest(
    Guid AccountId,
    string PaidOn,
    long? AmountCents = null);

public record CardTransactionCreatedDto(
    Guid Id,
    Guid? InstallmentGroupId,
    int CreatedCount,
    string FirstBillingMonth);
