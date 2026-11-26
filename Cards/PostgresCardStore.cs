using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace PersonalControl.Api.Cards;

// Acesso às tabelas de cartões, compras/parcelas e faturas em PostgreSQL.
// Todas as consultas filtram por user_id para isolamento estrito entre usuários.
public class PostgresCardStore(NpgsqlDataSource dataSource) : ICardStore
{
    public async Task<IReadOnlyList<CardDto>> ListCardsAsync(
        Guid userId,
        bool includeArchived = false,
        CancellationToken ct = default)
    {
        var cards = await LoadCardsRawAsync(userId, cardId: null, includeArchived, ct);
        return await EnrichCardsAsync(userId, cards, ct);
    }

    public async Task<CardDto?> GetCardAsync(Guid userId, Guid cardId, CancellationToken ct = default)
    {
        var cards = await LoadCardsRawAsync(userId, cardId, includeArchived: true, ct);
        if (cards.Count == 0)
            return null;

        var enriched = await EnrichCardsAsync(userId, cards, ct);
        return enriched[0];
    }

    public async Task<Guid> AddCardAsync(Guid userId, CardRequest request, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        await using var command = dataSource.CreateCommand("""
            INSERT INTO cards (
                id, user_id, name, kind, brand, issuer, last4, color,
                credit_limit_cents, closing_day, due_day,
                default_payment_account_id, linked_account_id, is_archived
            )
            VALUES (
                @id, @userId, @name, @kind, @brand, @issuer, @last4, @color,
                @creditLimit, @closingDay, @dueDay,
                @defaultPaymentAccountId, @linkedAccountId, @isArchived
            )
            """);

        BindCardParameters(command, id, userId, request);
        await command.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task<bool> UpdateCardAsync(Guid userId, Guid cardId, CardRequest request, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE cards
            SET name = @name,
                kind = @kind,
                brand = @brand,
                issuer = @issuer,
                last4 = @last4,
                color = @color,
                credit_limit_cents = @creditLimit,
                closing_day = @closingDay,
                due_day = @dueDay,
                default_payment_account_id = @defaultPaymentAccountId,
                linked_account_id = @linkedAccountId,
                is_archived = @isArchived
            WHERE id = @id AND user_id = @userId
            """);

        BindCardParameters(command, cardId, userId, request);
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> DeleteCardAsync(Guid userId, Guid cardId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand(
            "DELETE FROM cards WHERE id = @id AND user_id = @userId");
        command.Parameters.AddWithValue("id", cardId);
        command.Parameters.AddWithValue("userId", userId);
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    // ---------- Faturas ----------

    public async Task<CardInvoiceDto?> GetInvoiceAsync(
        Guid userId,
        Guid cardId,
        string billingMonth,
        CancellationToken ct = default)
    {
        await using var cardCmd = dataSource.CreateCommand("""
            SELECT name, kind, closing_day, due_day
            FROM cards
            WHERE id = @cardId AND user_id = @userId
            """);
        cardCmd.Parameters.AddWithValue("cardId", cardId);
        cardCmd.Parameters.AddWithValue("userId", userId);

        string cardName;
        int closingDay;
        int dueDay;

        await using (var reader = await cardCmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
                return null;

            if (reader.GetString(1) != "credit")
                return null;

            cardName = reader.GetString(0);
            closingDay = reader.GetInt16(2);
            dueDay = reader.GetInt16(3);
        }

        // Itens da fatura
        var items = new List<CardTransactionDto>();
        await using (var txCmd = dataSource.CreateCommand("""
            SELECT ct.id, ct.card_id, ct.type, ct.amount_cents, ct.description,
                   ct.occurred_on, ct.billing_month,
                   ct.category_id, c.name, c.color,
                   ct.installment_group_id, ct.installment_number, ct.installment_total
            FROM card_transactions ct
            LEFT JOIN categories c ON c.id = ct.category_id
            WHERE ct.user_id = @userId AND ct.card_id = @cardId AND ct.billing_month = @month
            ORDER BY ct.occurred_on DESC, ct.created_at DESC
            """))
        {
            txCmd.Parameters.AddWithValue("userId", userId);
            txCmd.Parameters.AddWithValue("cardId", cardId);
            txCmd.Parameters.AddWithValue("month", billingMonth);

            await using var reader = await txCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                items.Add(new CardTransactionDto(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    reader.GetString(4),
                    reader.GetFieldValue<DateOnly>(5).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetGuid(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetGuid(10),
                    reader.GetInt16(11),
                    reader.GetInt16(12)));
            }
        }

        // Pagamentos registrados (suporta pagamento total ou múltiplos parciais)
        var payments = new List<CardInvoicePaymentDto>();

        await using (var invCmd = dataSource.CreateCommand("""
            SELECT ci.id, ci.paid_amount_cents, ci.paid_on, ci.payment_account_id, a.name
            FROM card_invoices ci
            LEFT JOIN accounts a ON a.id = ci.payment_account_id
            WHERE ci.user_id = @userId AND ci.card_id = @cardId AND ci.billing_month = @month
            ORDER BY ci.paid_on ASC, ci.created_at ASC
            """))
        {
            invCmd.Parameters.AddWithValue("userId", userId);
            invCmd.Parameters.AddWithValue("cardId", cardId);
            invCmd.Parameters.AddWithValue("month", billingMonth);

            await using var reader = await invCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                payments.Add(new CardInvoicePaymentDto(
                    Id: reader.GetGuid(0),
                    AmountCents: reader.GetInt64(1),
                    PaidOn: reader.GetFieldValue<DateOnly>(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    AccountId: reader.IsDBNull(3) ? null : reader.GetGuid(3),
                    AccountName: reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        var totalCents = items.Sum(i => i.Type == "purchase" ? i.AmountCents : -i.AmountCents);
        var paidTotalCents = payments.Sum(p => p.AmountCents);
        var remainingCents = Math.Max(0, totalCents - paidTotalCents);
        var isFullyPaid = totalCents > 0 && paidTotalCents >= totalCents;
        var lastPayment = payments.LastOrDefault();

        var (closingDate, dueDate) = ComputeInvoiceDates(billingMonth, closingDay, dueDay);
        var status = ResolveInvoiceStatus(isFullyPaid, totalCents, closingDate, dueDate);

        return new CardInvoiceDto(
            CardId: cardId,
            CardName: cardName,
            BillingMonth: billingMonth,
            ClosingDate: closingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DueDate: dueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Status: status,
            TotalCents: totalCents,
            PaidAmountCents: paidTotalCents > 0 ? paidTotalCents : null,
            RemainingCents: remainingCents,
            PaidOn: lastPayment?.PaidOn,
            PaymentAccountId: lastPayment?.AccountId,
            PaymentAccountName: lastPayment?.AccountName,
            Payments: payments,
            Items: items);
    }

    // ---------- Lançamentos no cartão (compras, parcelas e estornos) ----------

    public async Task<CardTransactionCreatedDto> AddCardTransactionAsync(
        Guid userId,
        Guid cardId,
        CardTransactionRequest request,
        string firstBillingMonth,
        CancellationToken ct = default)
    {
        var occurredOn = DateOnly.ParseExact(request.OccurredOn, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var installments = request.Type == "purchase" ? Math.Clamp(request.Installments, 1, 48) : 1;
        var groupId = installments > 1 ? Guid.NewGuid() : (Guid?)null;
        var baseAmount = request.TotalAmountCents / installments;
        var remainder = request.TotalAmountCents % installments;

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using var command = new NpgsqlCommand("""
            INSERT INTO card_transactions (
                id, user_id, card_id, category_id, type, amount_cents,
                description, occurred_on, billing_month,
                installment_group_id, installment_number, installment_total
            )
            VALUES (
                @id, @userId, @cardId, @categoryId, @type, @amount,
                @description, @occurredOn, @billingMonth,
                @groupId, @instNumber, @instTotal
            )
            """, connection, transaction);

        var idParam = command.Parameters.Add("id", NpgsqlDbType.Uuid);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("cardId", cardId);
        command.Parameters.Add(new NpgsqlParameter("categoryId", NpgsqlDbType.Uuid)
        {
            Value = (object?)request.CategoryId ?? DBNull.Value,
        });
        command.Parameters.AddWithValue("type", request.Type);
        var amountParam = command.Parameters.Add("amount", NpgsqlDbType.Bigint);
        command.Parameters.AddWithValue("description", (request.Description ?? string.Empty).Trim());
        command.Parameters.AddWithValue("occurredOn", occurredOn);
        var billingMonthParam = command.Parameters.Add("billingMonth", NpgsqlDbType.Text);
        command.Parameters.Add(new NpgsqlParameter("groupId", NpgsqlDbType.Uuid)
        {
            Value = (object?)groupId ?? DBNull.Value,
        });
        var instNumberParam = command.Parameters.Add("instNumber", NpgsqlDbType.Smallint);
        command.Parameters.AddWithValue("instTotal", (short)installments);

        Guid firstId = Guid.Empty;
        for (var i = 1; i <= installments; i++)
        {
            var id = Guid.NewGuid();
            if (i == 1) firstId = id;

            idParam.Value = id;
            // O resto da divisão inteira vai na 1ª parcela para manter soma exata em centavos.
            amountParam.Value = i == 1 ? baseAmount + remainder : baseAmount;
            billingMonthParam.Value = ShiftMonth(firstBillingMonth, i - 1);
            instNumberParam.Value = (short)i;

            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return new CardTransactionCreatedDto(firstId, groupId, installments, firstBillingMonth);
    }

    public async Task<CardDeleteTxResult> DeleteCardTransactionAsync(
        Guid userId,
        Guid cardId,
        Guid transactionId,
        bool deleteGroup,
        CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        Guid? groupId = null;
        string billingMonth;

        await using (var findCmd = new NpgsqlCommand("""
            SELECT billing_month, installment_group_id
            FROM card_transactions
            WHERE id = @id AND card_id = @cardId AND user_id = @userId
            """, connection, transaction))
        {
            findCmd.Parameters.AddWithValue("id", transactionId);
            findCmd.Parameters.AddWithValue("cardId", cardId);
            findCmd.Parameters.AddWithValue("userId", userId);

            await using var reader = await findCmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return CardDeleteTxResult.NotFound;

            billingMonth = reader.GetString(0);
            groupId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
        }

        var useGroup = deleteGroup && groupId.HasValue;

        // Não permite excluir lançamentos que pertençam a uma fatura já paga.
        await using (var paidCheck = new NpgsqlCommand(
            useGroup
                ? """
                  SELECT EXISTS (
                      SELECT 1
                      FROM card_transactions ct
                      JOIN card_invoices ci ON ci.card_id = ct.card_id AND ci.billing_month = ct.billing_month
                      WHERE ct.installment_group_id = @groupId AND ct.card_id = @cardId AND ct.user_id = @userId
                  )
                  """
                : """
                  SELECT EXISTS (
                      SELECT 1
                      FROM card_invoices
                      WHERE card_id = @cardId AND user_id = @userId AND billing_month = @month
                  )
                  """,
            connection,
            transaction))
        {
            paidCheck.Parameters.AddWithValue("cardId", cardId);
            paidCheck.Parameters.AddWithValue("userId", userId);
            if (useGroup)
                paidCheck.Parameters.AddWithValue("groupId", groupId!.Value);
            else
                paidCheck.Parameters.AddWithValue("month", billingMonth);

            if (Convert.ToBoolean(await paidCheck.ExecuteScalarAsync(ct)))
                return CardDeleteTxResult.InPaidInvoice;
        }

        await using (var delCmd = new NpgsqlCommand(
            useGroup
                ? "DELETE FROM card_transactions WHERE installment_group_id = @groupId AND card_id = @cardId AND user_id = @userId"
                : "DELETE FROM card_transactions WHERE id = @id AND card_id = @cardId AND user_id = @userId",
            connection,
            transaction))
        {
            delCmd.Parameters.AddWithValue("cardId", cardId);
            delCmd.Parameters.AddWithValue("userId", userId);
            if (useGroup)
                delCmd.Parameters.AddWithValue("groupId", groupId!.Value);
            else
                delCmd.Parameters.AddWithValue("id", transactionId);

            await delCmd.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return CardDeleteTxResult.Deleted;
    }

    // ---------- Pagamento / Estorno de fatura ----------

    public async Task<PayInvoiceResult> PayInvoiceAsync(
        Guid userId,
        Guid cardId,
        string billingMonth,
        PayInvoiceRequest request,
        CancellationToken ct = default)
    {
        var paidOn = DateOnly.ParseExact(request.PaidOn, "yyyy-MM-dd", CultureInfo.InvariantCulture);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        string cardName;
        await using (var cardCmd = new NpgsqlCommand(
            "SELECT name FROM cards WHERE id = @cardId AND user_id = @userId AND kind = 'credit'",
            connection,
            transaction))
        {
            cardCmd.Parameters.AddWithValue("cardId", cardId);
            cardCmd.Parameters.AddWithValue("userId", userId);
            var scalar = await cardCmd.ExecuteScalarAsync(ct) as string;
            if (scalar is null)
                return PayInvoiceResult.CardNotFound;
            cardName = scalar;
        }

        long alreadyPaid;
        await using (var paidCmd = new NpgsqlCommand(
            "SELECT COALESCE(SUM(paid_amount_cents), 0)::BIGINT FROM card_invoices WHERE card_id = @cardId AND user_id = @userId AND billing_month = @month",
            connection,
            transaction))
        {
            paidCmd.Parameters.AddWithValue("cardId", cardId);
            paidCmd.Parameters.AddWithValue("userId", userId);
            paidCmd.Parameters.AddWithValue("month", billingMonth);
            alreadyPaid = Convert.ToInt64(await paidCmd.ExecuteScalarAsync(ct));
        }

        long invoiceTotal;
        await using (var sumCmd = new NpgsqlCommand("""
            SELECT COALESCE(SUM(CASE WHEN type = 'purchase' THEN amount_cents ELSE -amount_cents END), 0)::BIGINT
            FROM card_transactions
            WHERE card_id = @cardId AND user_id = @userId AND billing_month = @month
            """, connection, transaction))
        {
            sumCmd.Parameters.AddWithValue("cardId", cardId);
            sumCmd.Parameters.AddWithValue("userId", userId);
            sumCmd.Parameters.AddWithValue("month", billingMonth);
            invoiceTotal = Convert.ToInt64(await sumCmd.ExecuteScalarAsync(ct));
        }

        if (invoiceTotal <= 0)
            return PayInvoiceResult.EmptyInvoice;

        var remaining = Math.Max(0, invoiceTotal - alreadyPaid);
        if (remaining <= 0)
            return PayInvoiceResult.AlreadyPaid;

        var paidAmount = request.AmountCents ?? remaining;
        if (paidAmount <= 0)
            return PayInvoiceResult.EmptyInvoice;

        // Cria o débito na conta bancária com is_card_payment = TRUE (debita o saldo sem duplicar despesas nos gráficos).
        var paymentTxId = Guid.NewGuid();
        var formattedMonth = $"{billingMonth[5..7]}/{billingMonth[..4]}";
        var description = paidAmount < remaining
            ? $"Pagamento parcial fatura {cardName} ({formattedMonth})"
            : $"Pagamento fatura {cardName} ({formattedMonth})";

        await using (var txCmd = new NpgsqlCommand("""
            INSERT INTO transactions
                (id, user_id, account_id, category_id, transfer_account_id, type, amount_cents, description, occurred_on, is_card_payment)
            VALUES
                (@id, @userId, @accountId, NULL, NULL, 'expense', @amount, @description, @occurredOn, TRUE)
            """, connection, transaction))
        {
            txCmd.Parameters.AddWithValue("id", paymentTxId);
            txCmd.Parameters.AddWithValue("userId", userId);
            txCmd.Parameters.AddWithValue("accountId", request.AccountId);
            txCmd.Parameters.AddWithValue("amount", paidAmount);
            txCmd.Parameters.AddWithValue("description", description);
            txCmd.Parameters.AddWithValue("occurredOn", paidOn);
            await txCmd.ExecuteNonQueryAsync(ct);
        }

        await using (var invCmd = new NpgsqlCommand("""
            INSERT INTO card_invoices
                (id, user_id, card_id, billing_month, paid_amount_cents, paid_on, payment_account_id, payment_transaction_id)
            VALUES
                (@id, @userId, @cardId, @month, @paidAmount, @paidOn, @paymentAccountId, @paymentTxId)
            """, connection, transaction))
        {
            invCmd.Parameters.AddWithValue("id", Guid.NewGuid());
            invCmd.Parameters.AddWithValue("userId", userId);
            invCmd.Parameters.AddWithValue("cardId", cardId);
            invCmd.Parameters.AddWithValue("month", billingMonth);
            invCmd.Parameters.AddWithValue("paidAmount", paidAmount);
            invCmd.Parameters.AddWithValue("paidOn", paidOn);
            invCmd.Parameters.AddWithValue("paymentAccountId", request.AccountId);
            invCmd.Parameters.AddWithValue("paymentTxId", paymentTxId);
            await invCmd.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return PayInvoiceResult.Paid;
    }

    public async Task<bool> UnpayInvoiceAsync(
        Guid userId,
        Guid cardId,
        string billingMonth,
        CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var paymentTxIds = new List<Guid>();
        await using (var findCmd = new NpgsqlCommand("""
            SELECT payment_transaction_id
            FROM card_invoices
            WHERE user_id = @userId AND card_id = @cardId AND billing_month = @month
            """, connection, transaction))
        {
            findCmd.Parameters.AddWithValue("userId", userId);
            findCmd.Parameters.AddWithValue("cardId", cardId);
            findCmd.Parameters.AddWithValue("month", billingMonth);

            await using var reader = await findCmd.ExecuteReaderAsync(ct);
            var any = false;
            while (await reader.ReadAsync(ct))
            {
                any = true;
                if (!reader.IsDBNull(0))
                    paymentTxIds.Add(reader.GetGuid(0));
            }
            if (!any)
                return false;
        }

        await using (var delInv = new NpgsqlCommand("""
            DELETE FROM card_invoices
            WHERE user_id = @userId AND card_id = @cardId AND billing_month = @month
            """, connection, transaction))
        {
            delInv.Parameters.AddWithValue("userId", userId);
            delInv.Parameters.AddWithValue("cardId", cardId);
            delInv.Parameters.AddWithValue("month", billingMonth);
            await delInv.ExecuteNonQueryAsync(ct);
        }

        foreach (var txId in paymentTxIds)
        {
            await using var delTx = new NpgsqlCommand("""
                DELETE FROM transactions
                WHERE id = @id AND user_id = @userId AND is_card_payment = TRUE
                """, connection, transaction);
            delTx.Parameters.AddWithValue("id", txId);
            delTx.Parameters.AddWithValue("userId", userId);
            await delTx.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<bool> DeleteInvoicePaymentAsync(
        Guid userId,
        Guid cardId,
        string billingMonth,
        Guid paymentId,
        CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        Guid? paymentTxId;
        await using (var findCmd = new NpgsqlCommand("""
            SELECT payment_transaction_id
            FROM card_invoices
            WHERE id = @paymentId AND user_id = @userId AND card_id = @cardId AND billing_month = @month
            """, connection, transaction))
        {
            findCmd.Parameters.AddWithValue("paymentId", paymentId);
            findCmd.Parameters.AddWithValue("userId", userId);
            findCmd.Parameters.AddWithValue("cardId", cardId);
            findCmd.Parameters.AddWithValue("month", billingMonth);

            await using var reader = await findCmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return false;

            paymentTxId = reader.IsDBNull(0) ? null : reader.GetGuid(0);
        }

        await using (var delInv = new NpgsqlCommand("""
            DELETE FROM card_invoices
            WHERE id = @paymentId AND user_id = @userId AND card_id = @cardId AND billing_month = @month
            """, connection, transaction))
        {
            delInv.Parameters.AddWithValue("paymentId", paymentId);
            delInv.Parameters.AddWithValue("userId", userId);
            delInv.Parameters.AddWithValue("cardId", cardId);
            delInv.Parameters.AddWithValue("month", billingMonth);
            await delInv.ExecuteNonQueryAsync(ct);
        }

        if (paymentTxId.HasValue)
        {
            await using var delTx = new NpgsqlCommand("""
                DELETE FROM transactions
                WHERE id = @id AND user_id = @userId AND is_card_payment = TRUE
                """, connection, transaction);
            delTx.Parameters.AddWithValue("id", paymentTxId.Value);
            delTx.Parameters.AddWithValue("userId", userId);
            await delTx.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return true;
    }

    // ---------- Auxiliares ----------

    private sealed record RawCard(
        Guid Id,
        string Name,
        string Kind,
        string Brand,
        string Issuer,
        string? Last4,
        string Color,
        long? CreditLimitCents,
        long UsedLimitCents,
        int? ClosingDay,
        int? DueDay,
        Guid? DefaultPaymentAccountId,
        string? DefaultPaymentAccountName,
        Guid? LinkedAccountId,
        string? LinkedAccountName,
        long? LinkedAccountBalanceCents,
        bool IsArchived);

    private async Task<List<RawCard>> LoadCardsRawAsync(
        Guid userId,
        Guid? cardId,
        bool includeArchived,
        CancellationToken ct)
    {
        var sql = """
            SELECT
                c.id, c.name, c.kind, c.brand, c.issuer, c.last4, c.color,
                c.credit_limit_cents,
                GREATEST(0,
                    COALESCE((
                        SELECT SUM(CASE WHEN ct.type = 'purchase' THEN ct.amount_cents ELSE -ct.amount_cents END)::BIGINT
                        FROM card_transactions ct
                        WHERE ct.card_id = c.id AND ct.user_id = c.user_id
                    ), 0)
                    - COALESCE((
                        SELECT SUM(ci.paid_amount_cents)::BIGINT
                        FROM card_invoices ci
                        WHERE ci.card_id = c.id AND ci.user_id = c.user_id
                    ), 0)
                )::BIGINT AS used_limit_cents,
                c.closing_day, c.due_day,
                c.default_payment_account_id, pa.name,
                c.linked_account_id, la.name,
                CASE WHEN la.id IS NULL THEN NULL ELSE (
                    la.initial_balance_cents
                    + COALESCE((SELECT SUM(t.amount_cents)::BIGINT FROM transactions t WHERE t.account_id = la.id AND t.type = 'income'), 0)
                    - COALESCE((SELECT SUM(t.amount_cents)::BIGINT FROM transactions t WHERE t.account_id = la.id AND t.type IN ('expense', 'transfer')), 0)
                    + COALESCE((SELECT SUM(t.amount_cents)::BIGINT FROM transactions t WHERE t.transfer_account_id = la.id), 0)
                )::BIGINT END AS linked_balance_cents,
                c.is_archived
            FROM cards c
            LEFT JOIN accounts pa ON pa.id = c.default_payment_account_id
            LEFT JOIN accounts la ON la.id = c.linked_account_id
            WHERE c.user_id = @userId
            """;

        if (cardId.HasValue)
            sql += " AND c.id = @cardId";
        else if (!includeArchived)
            sql += " AND c.is_archived = FALSE";

        sql += " ORDER BY c.is_archived ASC, c.kind ASC, c.name ASC";

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("userId", userId);
        if (cardId.HasValue)
            command.Parameters.AddWithValue("cardId", cardId.Value);

        var list = new List<RawCard>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new RawCard(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7),
                reader.GetInt64(8),
                reader.IsDBNull(9) ? null : reader.GetInt16(9),
                reader.IsDBNull(10) ? null : reader.GetInt16(10),
                reader.IsDBNull(11) ? null : reader.GetGuid(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetGuid(13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetInt64(15),
                reader.GetBoolean(16)));
        }

        return list;
    }

    private async Task<List<CardDto>> EnrichCardsAsync(Guid userId, List<RawCard> rawCards, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var result = new List<CardDto>(rawCards.Count);

        foreach (var c in rawCards)
        {
            if (c.Kind != "credit" || c.ClosingDay is null || c.DueDay is null)
            {
                result.Add(new CardDto(
                    c.Id, c.Name, c.Kind, c.Brand, c.Issuer, c.Last4, c.Color,
                    CreditLimitCents: null,
                    UsedLimitCents: 0,
                    AvailableLimitCents: 0,
                    ClosingDay: null,
                    DueDay: null,
                    DefaultPaymentAccountId: null,
                    DefaultPaymentAccountName: null,
                    LinkedAccountId: c.LinkedAccountId,
                    LinkedAccountName: c.LinkedAccountName,
                    LinkedAccountBalanceCents: c.LinkedAccountBalanceCents,
                    CurrentInvoiceMonth: null,
                    CurrentInvoiceCents: 0,
                    CurrentInvoiceStatus: null,
                    IsArchived: c.IsArchived));
                continue;
            }

            var currentMonth = ResolveBillingMonth(today, c.ClosingDay.Value);
            var (invoiceTotal, isPaid) = await LoadInvoiceSummaryAsync(userId, c.Id, currentMonth, ct);
            var (closingDate, dueDate) = ComputeInvoiceDates(currentMonth, c.ClosingDay.Value, c.DueDay.Value);
            var status = ResolveInvoiceStatus(isPaid, invoiceTotal, closingDate, dueDate);

            var used = Math.Max(0, c.UsedLimitCents);
            var limit = c.CreditLimitCents ?? 0;
            var available = limit - used;

            result.Add(new CardDto(
                c.Id, c.Name, c.Kind, c.Brand, c.Issuer, c.Last4, c.Color,
                CreditLimitCents: c.CreditLimitCents,
                UsedLimitCents: used,
                AvailableLimitCents: available,
                ClosingDay: c.ClosingDay,
                DueDay: c.DueDay,
                DefaultPaymentAccountId: c.DefaultPaymentAccountId,
                DefaultPaymentAccountName: c.DefaultPaymentAccountName,
                LinkedAccountId: null,
                LinkedAccountName: null,
                LinkedAccountBalanceCents: null,
                CurrentInvoiceMonth: currentMonth,
                CurrentInvoiceCents: invoiceTotal,
                CurrentInvoiceStatus: status,
                IsArchived: c.IsArchived));
        }

        return result;
    }

    private async Task<(long TotalCents, bool IsPaid)> LoadInvoiceSummaryAsync(
        Guid userId,
        Guid cardId,
        string billingMonth,
        CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT
                COALESCE((
                    SELECT SUM(CASE WHEN type = 'purchase' THEN amount_cents ELSE -amount_cents END)::BIGINT
                    FROM card_transactions
                    WHERE user_id = @userId AND card_id = @cardId AND billing_month = @month
                ), 0)::BIGINT,
                COALESCE((
                    SELECT SUM(paid_amount_cents)::BIGINT
                    FROM card_invoices
                    WHERE user_id = @userId AND card_id = @cardId AND billing_month = @month
                ), 0)::BIGINT
            """);
        cmd.Parameters.AddWithValue("userId", userId);
        cmd.Parameters.AddWithValue("cardId", cardId);
        cmd.Parameters.AddWithValue("month", billingMonth);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var total = reader.GetInt64(0);
        var paid = reader.GetInt64(1);
        var isPaid = total > 0 && paid >= total;
        var openAmount = isPaid ? total : Math.Max(0, total - paid);
        return (openAmount, isPaid);
    }

    private static void BindCardParameters(NpgsqlCommand command, Guid id, Guid userId, CardRequest request)
    {
        var isCredit = request.Kind == "credit";
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("name", request.Name.Trim());
        command.Parameters.AddWithValue("kind", request.Kind);
        command.Parameters.AddWithValue("brand", request.Brand);
        command.Parameters.AddWithValue("issuer", (request.Issuer ?? string.Empty).Trim());
        command.Parameters.Add(new NpgsqlParameter("last4", NpgsqlDbType.Text)
        {
            Value = string.IsNullOrWhiteSpace(request.Last4) ? DBNull.Value : request.Last4.Trim(),
        });
        command.Parameters.AddWithValue("color", string.IsNullOrWhiteSpace(request.Color) ? "#7c3aed" : request.Color.Trim());
        command.Parameters.Add(new NpgsqlParameter("creditLimit", NpgsqlDbType.Bigint)
        {
            Value = isCredit && request.CreditLimitCents.HasValue ? request.CreditLimitCents.Value : DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("closingDay", NpgsqlDbType.Smallint)
        {
            Value = isCredit && request.ClosingDay.HasValue ? (short)request.ClosingDay.Value : DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("dueDay", NpgsqlDbType.Smallint)
        {
            Value = isCredit && request.DueDay.HasValue ? (short)request.DueDay.Value : DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("defaultPaymentAccountId", NpgsqlDbType.Uuid)
        {
            Value = isCredit && request.DefaultPaymentAccountId.HasValue ? request.DefaultPaymentAccountId.Value : DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("linkedAccountId", NpgsqlDbType.Uuid)
        {
            Value = !isCredit && request.LinkedAccountId.HasValue ? request.LinkedAccountId.Value : DBNull.Value,
        });
        command.Parameters.AddWithValue("isArchived", request.IsArchived);
    }

    // Regra de fechamento: a fatura fecha na virada para o dia de fechamento (ex.: dia 14 às 00:00).
    // Compras antes do dia de fechamento entram na fatura do mês atual;
    // compras a partir do dia de fechamento (melhor dia de compra) entram na fatura do mês seguinte.
    public static string ResolveBillingMonth(DateOnly date, int closingDay)
    {
        var daysInMonth = DateTime.DaysInMonth(date.Year, date.Month);
        var effectiveClosing = Math.Min(closingDay, daysInMonth);
        var target = date.Day < effectiveClosing
            ? new DateOnly(date.Year, date.Month, 1)
            : new DateOnly(date.Year, date.Month, 1).AddMonths(1);

        return target.ToString("yyyy-MM", CultureInfo.InvariantCulture);
    }

    private static (DateOnly ClosingDate, DateOnly DueDate) ComputeInvoiceDates(string billingMonth, int closingDay, int dueDay)
    {
        var monthStart = DateOnly.ParseExact(billingMonth + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var closingDate = new DateOnly(
            monthStart.Year,
            monthStart.Month,
            Math.Min(closingDay, DateTime.DaysInMonth(monthStart.Year, monthStart.Month)));

        var dueMonth = dueDay < closingDay ? monthStart.AddMonths(1) : monthStart;
        var dueDate = new DateOnly(
            dueMonth.Year,
            dueMonth.Month,
            Math.Min(dueDay, DateTime.DaysInMonth(dueMonth.Year, dueMonth.Month)));

        return (closingDate, dueDate);
    }

    private static string ResolveInvoiceStatus(bool isPaid, long totalCents, DateOnly closingDate, DateOnly dueDate)
    {
        if (isPaid)
            return "paid";

        var today = DateOnly.FromDateTime(DateTime.Today);
        if (today > dueDate && totalCents > 0)
            return "overdue";

        if (today >= closingDate)
            return "closed";

        return "open";
    }

    private static string ShiftMonth(string month, int delta)
    {
        var start = DateOnly.ParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return start.AddMonths(delta).ToString("yyyy-MM", CultureInfo.InvariantCulture);
    }
}
