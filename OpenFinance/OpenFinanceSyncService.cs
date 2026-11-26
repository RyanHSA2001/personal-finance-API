using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using PersonalControl.Api.Cards;
using PersonalControl.Api.Finance;

namespace PersonalControl.Api.OpenFinance;

// Gerencia credenciais criptografadas (BYOK), conexões Open Finance (Pluggy) e sincronização
// de contas, cartões, faturas, lançamentos e investimentos.
public class OpenFinanceSyncService(
    NpgsqlDataSource dataSource,
    PluggyApiClient pluggy,
    IFinanceStore financeStore,
    SecretProtector protector,
    IOptions<PluggyOptions> serverOptions)
{
    private static readonly Regex TrailingDigits = new(@"(\d{4})\D*$");
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ItemLocks = new(StringComparer.Ordinal);
    private readonly PluggyOptions _serverOptions = serverOptions.Value;

    // ---------- Credenciais criptografadas por usuário ----------

    public async Task<OpenFinanceStatusDto> GetStatusAsync(Guid userId, CancellationToken ct = default)
    {
        string? clientIdPreview = null;
        DateTimeOffset? updatedAt = null;

        await using (var credCmd = dataSource.CreateCommand("""
            SELECT client_id_preview, updated_at
            FROM user_pluggy_credentials
            WHERE user_id = @userId
            """))
        {
            credCmd.Parameters.AddWithValue("userId", userId);
            await using var reader = await credCmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                clientIdPreview = reader.GetString(0);
                updatedAt = reader.GetFieldValue<DateTimeOffset>(1);
            }
        }

        await using var cmd = dataSource.CreateCommand("""
            SELECT
                (SELECT COUNT(*)::INT FROM pluggy_items WHERE user_id = @userId),
                (SELECT COUNT(*)::INT FROM pluggy_account_links WHERE user_id = @userId AND local_account_id IS NOT NULL),
                (SELECT COUNT(*)::INT FROM pluggy_account_links WHERE user_id = @userId AND local_card_id IS NOT NULL)
            """);
        cmd.Parameters.AddWithValue("userId", userId);

        await using var countReader = await cmd.ExecuteReaderAsync(ct);
        await countReader.ReadAsync(ct);

        var source = clientIdPreview is not null
            ? "user"
            : _serverOptions.IsConfigured ? "server" : "none";

        return new OpenFinanceStatusDto(
            Configured: source != "none",
            CredentialsSource: source,
            ClientIdPreview: clientIdPreview,
            CredentialsUpdatedAt: updatedAt,
            ItemCount: countReader.GetInt32(0),
            LinkedAccountsCount: countReader.GetInt32(1),
            LinkedCardsCount: countReader.GetInt32(2));
    }

    public async Task SaveUserCredentialsAsync(
        Guid userId,
        SavePluggyCredentialsRequest request,
        CancellationToken ct = default)
    {
        var pair = new PluggyCredentialsPair(request.ClientId.Trim(), request.ClientSecret.Trim());

        // Valida na API do Pluggy ANTES de salvar no banco.
        await pluggy.ValidateCredentialsAsync(pair, ct);

        var clientIdEnc = protector.Protect(userId, pair.ClientId);
        var clientSecretEnc = protector.Protect(userId, pair.ClientSecret);
        var preview = MaskClientId(pair.ClientId);

        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO user_pluggy_credentials (user_id, client_id_enc, client_secret_enc, client_id_preview, updated_at)
            VALUES (@userId, @clientIdEnc, @clientSecretEnc, @preview, NOW())
            ON CONFLICT (user_id) DO UPDATE SET
                client_id_enc = EXCLUDED.client_id_enc,
                client_secret_enc = EXCLUDED.client_secret_enc,
                client_id_preview = EXCLUDED.client_id_preview,
                updated_at = NOW()
            """);
        cmd.Parameters.AddWithValue("userId", userId);
        cmd.Parameters.AddWithValue("clientIdEnc", clientIdEnc);
        cmd.Parameters.AddWithValue("clientSecretEnc", clientSecretEnc);
        cmd.Parameters.AddWithValue("preview", preview);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeleteUserCredentialsAsync(Guid userId, CancellationToken ct = default)
    {
        var existing = await LoadUserCredentialsAsync(userId, ct);
        if (existing is not null)
            pluggy.InvalidateCache(existing);

        await using var cmd = dataSource.CreateCommand(
            "DELETE FROM user_pluggy_credentials WHERE user_id = @userId");
        cmd.Parameters.AddWithValue("userId", userId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<string> CreateConnectTokenAsync(Guid userId, string? itemId, CancellationToken ct = default)
    {
        var creds = await RequireCredentialsAsync(userId, ct);
        return await pluggy.CreateConnectTokenAsync(creds, itemId, userId.ToString(), ct);
    }

    private async Task<PluggyCredentialsPair> RequireCredentialsAsync(Guid userId, CancellationToken ct)
    {
        var userCreds = await LoadUserCredentialsAsync(userId, ct);
        if (userCreds is not null)
            return userCreds;

        if (_serverOptions.IsConfigured)
            return new PluggyCredentialsPair(_serverOptions.ClientId, _serverOptions.ClientSecret);

        throw new PluggyApiException(
            "Configure seu Client ID e Client Secret do Pluggy na tela de Open Finance antes de continuar.",
            400);
    }

    private async Task<PluggyCredentialsPair?> LoadUserCredentialsAsync(Guid userId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT client_id_enc, client_secret_enc
            FROM user_pluggy_credentials
            WHERE user_id = @userId
            """);
        cmd.Parameters.AddWithValue("userId", userId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var clientId = protector.Unprotect(userId, reader.GetString(0));
        var clientSecret = protector.Unprotect(userId, reader.GetString(1));
        return new PluggyCredentialsPair(clientId, clientSecret);
    }

    private static string MaskClientId(string clientId)
    {
        if (clientId.Length <= 8)
            return "••••••••";

        return $"{clientId[..4]}••••{clientId[^4..]}";
    }

    public async Task<IReadOnlyList<PluggyItemDto>> ListItemsAsync(Guid userId, CancellationToken ct = default)
    {
        var items = new List<PluggyItemDto>();
        var linksByItem = new Dictionary<Guid, List<PluggyAccountLinkDto>>();

        await using (var linkCmd = dataSource.CreateCommand("""
            SELECT l.id, l.pluggy_item_row_id, l.pluggy_account_id, l.pluggy_type, l.pluggy_subtype,
                   l.name, l.masked_number, l.currency_code, l.remote_balance_cents,
                   l.local_account_id, a.name,
                   l.local_card_id, c.name,
                   l.last_synced_at
            FROM pluggy_account_links l
            LEFT JOIN accounts a ON a.id = l.local_account_id
            LEFT JOIN cards c ON c.id = l.local_card_id
            WHERE l.user_id = @userId
            ORDER BY l.pluggy_type, l.name
            """))
        {
            linkCmd.Parameters.AddWithValue("userId", userId);
            await using var reader = await linkCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var itemRowId = reader.GetGuid(1);
                if (!linksByItem.TryGetValue(itemRowId, out var list))
                {
                    list = [];
                    linksByItem[itemRowId] = list;
                }

                list.Add(new PluggyAccountLinkDto(
                    Id: reader.GetGuid(0),
                    PluggyAccountId: reader.GetString(2),
                    PluggyType: reader.GetString(3),
                    PluggySubtype: reader.GetString(4),
                    Name: reader.GetString(5),
                    MaskedNumber: reader.IsDBNull(6) ? null : reader.GetString(6),
                    CurrencyCode: reader.GetString(7),
                    RemoteBalanceCents: reader.GetInt64(8),
                    LocalAccountId: reader.IsDBNull(9) ? null : reader.GetGuid(9),
                    LocalAccountName: reader.IsDBNull(10) ? null : reader.GetString(10),
                    LocalCardId: reader.IsDBNull(11) ? null : reader.GetGuid(11),
                    LocalCardName: reader.IsDBNull(12) ? null : reader.GetString(12),
                    LastSyncedAt: reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13)));
            }
        }

        await using (var itemCmd = dataSource.CreateCommand("""
            SELECT id, pluggy_item_id, connector_name, connector_image_url, connector_color,
                   status, execution_status, last_error, last_synced_at
            FROM pluggy_items
            WHERE user_id = @userId
            ORDER BY created_at DESC
            """))
        {
            itemCmd.Parameters.AddWithValue("userId", userId);
            await using var reader = await itemCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetGuid(0);
                items.Add(new PluggyItemDto(
                    Id: id,
                    PluggyItemId: reader.GetString(1),
                    ConnectorName: reader.GetString(2),
                    ConnectorImageUrl: reader.IsDBNull(3) ? null : reader.GetString(3),
                    ConnectorColor: reader.IsDBNull(4) ? null : reader.GetString(4),
                    Status: reader.GetString(5),
                    ExecutionStatus: reader.IsDBNull(6) ? null : reader.GetString(6),
                    LastError: reader.IsDBNull(7) ? null : reader.GetString(7),
                    LastSyncedAt: reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
                    Links: linksByItem.GetValueOrDefault(id) ?? []));
            }
        }

        return items;
    }

    public async Task<SyncItemResultDto> LinkAndSyncItemAsync(
        Guid userId,
        string pluggyItemId,
        int days,
        CancellationToken ct = default)
    {
        var creds = await RequireCredentialsAsync(userId, ct);
        var remoteItem = await pluggy.GetItemAsync(creds, pluggyItemId, ct);
        var itemRowId = await UpsertItemRowAsync(userId, remoteItem, ct);
        return await SyncItemCoreAsync(userId, creds, itemRowId, remoteItem, days, ct);
    }

    public async Task<SyncItemResultDto?> SyncExistingItemAsync(
        Guid userId,
        Guid itemRowId,
        int days,
        bool triggerRemoteRefresh,
        CancellationToken ct = default)
    {
        var pluggyItemId = await GetPluggyItemIdAsync(userId, itemRowId, ct);
        if (pluggyItemId is null)
            return null;

        var itemLock = ItemLocks.GetOrAdd(pluggyItemId, _ => new SemaphoreSlim(1, 1));
        await itemLock.WaitAsync(ct);
        try
        {
            var creds = await RequireCredentialsAsync(userId, ct);
            if (triggerRemoteRefresh)
                await pluggy.TriggerItemUpdateAsync(creds, pluggyItemId, ct);

            var remoteItem = await pluggy.GetItemAsync(creds, pluggyItemId, ct);

            // Se disparou atualização no banco, aguarda até ~30s enquanto o Pluggy estiver em UPDATING
            // para já devolver os lançamentos novos sem precisar clicar manualmente de novo.
            if (triggerRemoteRefresh)
            {
                const int maxPolls = 10;
                for (var attempt = 0; attempt < maxPolls && IsUpdating(remoteItem.Status); attempt++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                    remoteItem = await pluggy.GetItemAsync(creds, pluggyItemId, ct);
                }
            }

            await UpsertItemRowAsync(userId, remoteItem, ct);
            return await SyncItemCoreAsync(userId, creds, itemRowId, remoteItem, days, ct);
        }
        finally
        {
            itemLock.Release();
        }
    }

    // Usado pelo webhook do Pluggy: localiza o item já vinculado no banco e puxa dados autenticados direto da API Pluggy.
    public async Task<int> SyncByPluggyItemIdAsync(string pluggyItemId, int days, CancellationToken ct = default)
    {
        var targets = new List<(Guid UserId, Guid ItemRowId)>();
        await using (var cmd = dataSource.CreateCommand("""
            SELECT user_id, id
            FROM pluggy_items
            WHERE pluggy_item_id = @pluggyItemId
            """))
        {
            cmd.Parameters.AddWithValue("pluggyItemId", pluggyItemId.Trim());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                targets.Add((reader.GetGuid(0), reader.GetGuid(1)));
        }

        var synced = 0;
        foreach (var (userId, itemRowId) in targets)
        {
            await SyncExistingItemAsync(userId, itemRowId, days, triggerRemoteRefresh: false, ct);
            synced++;
        }

        return synced;
    }

    public async Task<IReadOnlyList<PluggySyncTarget>> ListAllSyncTargetsAsync(CancellationToken ct = default)
    {
        var list = new List<PluggySyncTarget>();
        await using var cmd = dataSource.CreateCommand("""
            SELECT user_id, id, pluggy_item_id
            FROM pluggy_items
            ORDER BY last_synced_at ASC NULLS FIRST
            """);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new PluggySyncTarget(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2)));

        return list;
    }

    private static bool IsUpdating(string? status) =>
        string.Equals(status, "UPDATING", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<PluggyInvestmentDto>?> ListItemInvestmentsAsync(
        Guid userId,
        Guid itemRowId,
        CancellationToken ct = default)
    {
        var pluggyItemId = await GetPluggyItemIdAsync(userId, itemRowId, ct);
        if (pluggyItemId is null)
            return null;

        var creds = await RequireCredentialsAsync(userId, ct);
        var remote = await pluggy.GetInvestmentsAsync(creds, pluggyItemId, ct);
        return remote.Select(i => new PluggyInvestmentDto(
            Id: i.Id,
            Name: string.IsNullOrWhiteSpace(i.Name) ? "Investimento" : i.Name.Trim(),
            Type: i.Type ?? "OTHER",
            Subtype: i.Subtype,
            BalanceCents: ToCents(i.Balance ?? 0m),
            ProfitCents: i.AmountProfit.HasValue ? ToCents(i.AmountProfit.Value) : null,
            InstitutionName: i.Institution?.Name)).ToList();
    }

    public async Task<bool> DeleteItemAsync(Guid userId, Guid itemRowId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "DELETE FROM pluggy_items WHERE id = @id AND user_id = @userId");
        cmd.Parameters.AddWithValue("id", itemRowId);
        cmd.Parameters.AddWithValue("userId", userId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<string?> GetPluggyItemIdAsync(Guid userId, Guid itemRowId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT pluggy_item_id FROM pluggy_items WHERE id = @id AND user_id = @userId");
        cmd.Parameters.AddWithValue("id", itemRowId);
        cmd.Parameters.AddWithValue("userId", userId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    // ---------- Núcleo de sincronização ----------

    private async Task<SyncItemResultDto> SyncItemCoreAsync(
        Guid userId,
        PluggyCredentialsPair creds,
        Guid itemRowId,
        PluggyRemoteItem remoteItem,
        int days,
        CancellationToken ct)
    {
        await financeStore.EnsureDefaultCategoriesAsync(userId, ct);
        var categories = await financeStore.ListCategoriesAsync(userId, ct);

        var clampedDays = Math.Clamp(days, 7, 365);
        var toDate = DateOnly.FromDateTime(DateTime.Today);
        var fromDate = toDate.AddDays(-clampedDays);

        var remoteAccounts = await pluggy.GetAccountsAsync(creds, remoteItem.Id, ct);
        var connectorName = remoteItem.Connector?.Name?.Trim() ?? "Banco";
        var connectorColor = NormalizeHexColor(remoteItem.Connector?.PrimaryColor, "#7c3aed");

        var accountsSynced = 0;
        var cardsSynced = 0;
        var txImported = 0;
        var cardTxImported = 0;

        foreach (var remoteAcc in remoteAccounts)
        {
            var isCreditCard =
                string.Equals(remoteAcc.Type, "CREDIT", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(remoteAcc.Subtype, "CREDIT_CARD", StringComparison.OrdinalIgnoreCase);

            if (isCreditCard)
            {
                var imported = await SyncCreditCardAccountAsync(
                    userId, creds, itemRowId, connectorName, connectorColor, remoteAcc, fromDate, toDate, categories, ct);
                cardsSynced++;
                cardTxImported += imported;
            }
            else
            {
                var imported = await SyncBankAccountAsync(
                    userId, creds, itemRowId, connectorName, remoteAcc, fromDate, toDate, categories, ct);
                accountsSynced++;
                txImported += imported;
            }
        }

        // Investimentos (consulta opcional; falha silenciosa se o conector não suportar)
        var investments = await pluggy.GetInvestmentsAsync(creds, remoteItem.Id, ct);
        var investmentsTotalCents = investments.Sum(i => ToCents(i.Balance ?? 0m));

        await using (var updateCmd = dataSource.CreateCommand("""
            UPDATE pluggy_items
            SET last_synced_at = NOW()
            WHERE id = @id AND user_id = @userId
            """))
        {
            updateCmd.Parameters.AddWithValue("id", itemRowId);
            updateCmd.Parameters.AddWithValue("userId", userId);
            await updateCmd.ExecuteNonQueryAsync(ct);
        }

        return new SyncItemResultDto(
            ItemRowId: itemRowId,
            PluggyItemId: remoteItem.Id,
            ConnectorName: connectorName,
            Status: remoteItem.Status ?? "UPDATED",
            AccountsSynced: accountsSynced,
            CardsSynced: cardsSynced,
            TransactionsImported: txImported,
            CardTransactionsImported: cardTxImported,
            InvestmentsCount: investments.Count,
            InvestmentsTotalCents: investmentsTotalCents);
    }

    private async Task<int> SyncBankAccountAsync(
        Guid userId,
        PluggyCredentialsPair creds,
        Guid itemRowId,
        string connectorName,
        PluggyRemoteAccount remoteAcc,
        DateOnly fromDate,
        DateOnly toDate,
        IReadOnlyList<CategoryDto> categories,
        CancellationToken ct)
    {
        var remoteBalanceCents = ToCents(remoteAcc.Balance ?? 0m);
        var displayName = BuildDisplayName(connectorName, remoteAcc.MarketingName ?? remoteAcc.Name, "Conta");
        var maskedNumber = MaskAccountNumber(remoteAcc.Number);
        var accountKind = MapAccountKind(remoteAcc.Subtype);

        var (existingLocalAccountId, _) = await FindExistingLinkAsync(userId, remoteAcc.Id, ct);
        var createdNow = false;
        Guid localAccountId;

        if (existingLocalAccountId.HasValue)
        {
            localAccountId = existingLocalAccountId.Value;
        }
        else
        {
            localAccountId = await financeStore.AddAccountAsync(
                userId,
                new AccountRequest(displayName, accountKind, 0),
                ct);
            createdNow = true;
        }

        await UpsertAccountLinkAsync(
            userId,
            itemRowId,
            remoteAcc,
            displayName,
            maskedNumber,
            remoteBalanceCents,
            localAccountId: localAccountId,
            localCardId: null,
            ct);

        var remoteTxs = await pluggy.GetTransactionsAsync(creds, remoteAcc.Id, fromDate, toDate, ct);
        var imported = 0;

        if (remoteTxs.Count > 0)
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);

            await using var insertCmd = new NpgsqlCommand("""
                INSERT INTO transactions
                    (id, user_id, account_id, category_id, type, amount_cents, description, occurred_on, external_id)
                VALUES
                    (@id, @userId, @accountId, @categoryId, @type, @amount, @description, @occurredOn, @externalId)
                ON CONFLICT (account_id, external_id) WHERE external_id IS NOT NULL DO NOTHING
                """, connection, transaction);

            var idParam = insertCmd.Parameters.Add("id", NpgsqlDbType.Uuid);
            insertCmd.Parameters.AddWithValue("userId", userId);
            insertCmd.Parameters.AddWithValue("accountId", localAccountId);
            var catParam = insertCmd.Parameters.Add("categoryId", NpgsqlDbType.Uuid);
            var typeParam = insertCmd.Parameters.Add("type", NpgsqlDbType.Text);
            var amountParam = insertCmd.Parameters.Add("amount", NpgsqlDbType.Bigint);
            var descParam = insertCmd.Parameters.Add("description", NpgsqlDbType.Text);
            var dateParam = insertCmd.Parameters.Add("occurredOn", NpgsqlDbType.Date);
            var extParam = insertCmd.Parameters.Add("externalId", NpgsqlDbType.Text);

            foreach (var tx in remoteTxs)
            {
                var amountCents = Math.Abs(ToCents(tx.Amount));
                if (amountCents <= 0)
                    continue;

                var isIncome = string.Equals(tx.Type, "CREDIT", StringComparison.OrdinalIgnoreCase)
                    || (string.IsNullOrWhiteSpace(tx.Type) && tx.Amount > 0);
                var localType = isIncome ? "income" : "expense";
                var desc = Truncate((tx.Description ?? tx.DescriptionRaw ?? string.Empty).Trim(), 200);
                var catId = MatchCategory(categories, localType, tx.Category, desc);

                idParam.Value = Guid.NewGuid();
                catParam.Value = (object?)catId ?? DBNull.Value;
                typeParam.Value = localType;
                amountParam.Value = amountCents;
                descParam.Value = desc;
                dateParam.Value = DateOnly.FromDateTime(tx.Date.UtcDateTime);
                extParam.Value = $"pluggy:{tx.Id}";

                imported += await insertCmd.ExecuteNonQueryAsync(ct);
            }

            // Na primeira sincronização, calibra o saldo inicial para que o saldo local bata com o saldo real do banco.
            if (createdNow)
            {
                await using var sumCmd = new NpgsqlCommand("""
                    SELECT
                        COALESCE(SUM(CASE WHEN type = 'income' THEN amount_cents ELSE -amount_cents END), 0)::BIGINT
                    FROM transactions
                    WHERE account_id = @accountId AND user_id = @userId
                    """, connection, transaction);
                sumCmd.Parameters.AddWithValue("accountId", localAccountId);
                sumCmd.Parameters.AddWithValue("userId", userId);
                var netImported = Convert.ToInt64(await sumCmd.ExecuteScalarAsync(ct));

                await using var adjCmd = new NpgsqlCommand("""
                    UPDATE accounts
                    SET initial_balance_cents = @initial
                    WHERE id = @accountId AND user_id = @userId
                    """, connection, transaction);
                adjCmd.Parameters.AddWithValue("initial", remoteBalanceCents - netImported);
                adjCmd.Parameters.AddWithValue("accountId", localAccountId);
                adjCmd.Parameters.AddWithValue("userId", userId);
                await adjCmd.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
        else if (createdNow && remoteBalanceCents != 0)
        {
            await using var adjCmd = dataSource.CreateCommand("""
                UPDATE accounts SET initial_balance_cents = @initial
                WHERE id = @accountId AND user_id = @userId
                """);
            adjCmd.Parameters.AddWithValue("initial", remoteBalanceCents);
            adjCmd.Parameters.AddWithValue("accountId", localAccountId);
            adjCmd.Parameters.AddWithValue("userId", userId);
            await adjCmd.ExecuteNonQueryAsync(ct);
        }

        return imported;
    }

    private async Task<int> SyncCreditCardAccountAsync(
        Guid userId,
        PluggyCredentialsPair creds,
        Guid itemRowId,
        string connectorName,
        string connectorColor,
        PluggyRemoteAccount remoteAcc,
        DateOnly fromDate,
        DateOnly toDate,
        IReadOnlyList<CategoryDto> categories,
        CancellationToken ct)
    {
        var creditData = remoteAcc.CreditData;
        var remoteBalanceCents = ToCents(remoteAcc.Balance ?? 0m);
        var displayName = BuildDisplayName(connectorName, remoteAcc.MarketingName ?? remoteAcc.Name, "Cartão");
        // Segurança: extrai apenas os 4 últimos dígitos (nunca armazena número completo).
        var last4 = ExtractLast4(remoteAcc.Number);
        var maskedNumber = last4 is not null ? $"•••• {last4}" : null;

        var brand = MapCardBrand(creditData?.Brand);
        var limitCents = creditData?.CreditLimit is > 0 ? ToCents(creditData.CreditLimit.Value) : 100_000L;
        var dueDay = creditData?.BalanceDueDate?.Day ?? 17;
        var closingDay = creditData?.BalanceCloseDate?.Day ?? (dueDay > 7 ? dueDay - 7 : Math.Max(1, 28 + dueDay - 7));

        var (_, existingLocalCardId) = await FindExistingLinkAsync(userId, remoteAcc.Id, ct);
        Guid localCardId;

        if (existingLocalCardId.HasValue)
        {
            localCardId = existingLocalCardId.Value;
            if (creditData?.CreditLimit is > 0)
            {
                await using var upd = dataSource.CreateCommand("""
                    UPDATE cards SET credit_limit_cents = @limit
                    WHERE id = @id AND user_id = @userId
                    """);
                upd.Parameters.AddWithValue("limit", limitCents);
                upd.Parameters.AddWithValue("id", localCardId);
                upd.Parameters.AddWithValue("userId", userId);
                await upd.ExecuteNonQueryAsync(ct);
            }
        }
        else
        {
            localCardId = Guid.NewGuid();
            await using var ins = dataSource.CreateCommand("""
                INSERT INTO cards (
                    id, user_id, name, kind, brand, issuer, last4, color,
                    credit_limit_cents, closing_day, due_day
                )
                VALUES (
                    @id, @userId, @name, 'credit', @brand, @issuer, @last4, @color,
                    @limit, @closingDay, @dueDay
                )
                """);
            ins.Parameters.AddWithValue("id", localCardId);
            ins.Parameters.AddWithValue("userId", userId);
            ins.Parameters.AddWithValue("name", Truncate(displayName, 80));
            ins.Parameters.AddWithValue("brand", brand);
            ins.Parameters.AddWithValue("issuer", Truncate(connectorName, 60));
            ins.Parameters.Add(new NpgsqlParameter("last4", NpgsqlDbType.Text)
            {
                Value = (object?)last4 ?? DBNull.Value,
            });
            ins.Parameters.AddWithValue("color", connectorColor);
            ins.Parameters.AddWithValue("limit", limitCents);
            ins.Parameters.AddWithValue("closingDay", (short)Math.Clamp(closingDay, 1, 31));
            ins.Parameters.AddWithValue("dueDay", (short)Math.Clamp(dueDay, 1, 31));
            await ins.ExecuteNonQueryAsync(ct);
        }

        await UpsertAccountLinkAsync(
            userId,
            itemRowId,
            remoteAcc,
            displayName,
            maskedNumber,
            remoteBalanceCents,
            localAccountId: null,
            localCardId: localCardId,
            ct);

        // Mapeia faturas do Pluggy (billId -> AAAA-MM) quando disponíveis.
        var bills = await pluggy.GetBillsAsync(creds, remoteAcc.Id, ct);
        var billMonthById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var bill in bills)
        {
            if (bill.DueDate.HasValue)
                billMonthById[bill.Id] = bill.DueDate.Value.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        }

        var remoteTxs = await pluggy.GetTransactionsAsync(creds, remoteAcc.Id, fromDate, toDate, ct);
        if (remoteTxs.Count == 0)
            return 0;

        var imported = 0;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using var cmd = new NpgsqlCommand("""
            INSERT INTO card_transactions (
                id, user_id, card_id, category_id, type, amount_cents,
                description, occurred_on, billing_month,
                installment_number, installment_total, external_id
            )
            VALUES (
                @id, @userId, @cardId, @categoryId, @type, @amount,
                @description, @occurredOn, @billingMonth,
                @instNumber, @instTotal, @externalId
            )
            ON CONFLICT (card_id, external_id) WHERE external_id IS NOT NULL DO NOTHING
            """, connection, transaction);

        var idParam = cmd.Parameters.Add("id", NpgsqlDbType.Uuid);
        cmd.Parameters.AddWithValue("userId", userId);
        cmd.Parameters.AddWithValue("cardId", localCardId);
        var catParam = cmd.Parameters.Add("categoryId", NpgsqlDbType.Uuid);
        var typeParam = cmd.Parameters.Add("type", NpgsqlDbType.Text);
        var amountParam = cmd.Parameters.Add("amount", NpgsqlDbType.Bigint);
        var descParam = cmd.Parameters.Add("description", NpgsqlDbType.Text);
        var dateParam = cmd.Parameters.Add("occurredOn", NpgsqlDbType.Date);
        var monthParam = cmd.Parameters.Add("billingMonth", NpgsqlDbType.Text);
        var instNumParam = cmd.Parameters.Add("instNumber", NpgsqlDbType.Smallint);
        var instTotParam = cmd.Parameters.Add("instTotal", NpgsqlDbType.Smallint);
        var extParam = cmd.Parameters.Add("externalId", NpgsqlDbType.Text);

        foreach (var tx in remoteTxs)
        {
            var amountCents = Math.Abs(ToCents(tx.Amount));
            if (amountCents <= 0)
                continue;

            var desc = Truncate((tx.Description ?? tx.DescriptionRaw ?? string.Empty).Trim(), 200);
            var isCreditEntry = string.Equals(tx.Type, "CREDIT", StringComparison.OrdinalIgnoreCase) || tx.Amount < 0;

            // Ignora linhas de pagamento da própria fatura no extrato do cartão para não distorcer gastos.
            if (isCreditEntry && IsCardPaymentDescription(desc))
                continue;

            var localType = isCreditEntry ? "refund" : "purchase";
            var occurredOn = DateOnly.FromDateTime(tx.Date.UtcDateTime);

            string billingMonth;
            if (tx.CreditCardMetadata?.BillId is { } billId && billMonthById.TryGetValue(billId, out var fromBill))
                billingMonth = fromBill;
            else
                billingMonth = PostgresCardStore.ResolveBillingMonth(occurredOn, closingDay);

            var totalInst = Math.Clamp(tx.CreditCardMetadata?.TotalInstallments ?? 1, 1, 48);
            var instNum = Math.Clamp(tx.CreditCardMetadata?.InstallmentNumber ?? 1, 1, totalInst);
            var catId = MatchCategory(categories, "expense", tx.Category, desc);

            idParam.Value = Guid.NewGuid();
            catParam.Value = (object?)catId ?? DBNull.Value;
            typeParam.Value = localType;
            amountParam.Value = amountCents;
            descParam.Value = desc;
            dateParam.Value = occurredOn;
            monthParam.Value = billingMonth;
            instNumParam.Value = (short)instNum;
            instTotParam.Value = (short)totalInst;
            extParam.Value = $"pluggy:{tx.Id}";

            imported += await cmd.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return imported;
    }

    // ---------- Helpers de persistência ----------

    private async Task<Guid> UpsertItemRowAsync(Guid userId, PluggyRemoteItem remoteItem, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO pluggy_items (
                id, user_id, pluggy_item_id, connector_name, connector_image_url, connector_color,
                status, execution_status, last_error
            )
            VALUES (
                @id, @userId, @pluggyItemId, @connectorName, @connectorImageUrl, @connectorColor,
                @status, @executionStatus, @lastError
            )
            ON CONFLICT (user_id, pluggy_item_id) DO UPDATE SET
                connector_name = EXCLUDED.connector_name,
                connector_image_url = EXCLUDED.connector_image_url,
                connector_color = EXCLUDED.connector_color,
                status = EXCLUDED.status,
                execution_status = EXCLUDED.execution_status,
                last_error = EXCLUDED.last_error
            RETURNING id
            """);

        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("userId", userId);
        cmd.Parameters.AddWithValue("pluggyItemId", remoteItem.Id);
        cmd.Parameters.AddWithValue("connectorName", remoteItem.Connector?.Name?.Trim() ?? "Instituição");
        cmd.Parameters.Add(new NpgsqlParameter("connectorImageUrl", NpgsqlDbType.Text)
        {
            Value = (object?)remoteItem.Connector?.ImageUrl ?? DBNull.Value,
        });
        cmd.Parameters.Add(new NpgsqlParameter("connectorColor", NpgsqlDbType.Text)
        {
            Value = (object?)NormalizeHexColor(remoteItem.Connector?.PrimaryColor, "#7c3aed") ?? DBNull.Value,
        });
        cmd.Parameters.AddWithValue("status", remoteItem.Status ?? "UNKNOWN");
        cmd.Parameters.Add(new NpgsqlParameter("executionStatus", NpgsqlDbType.Text)
        {
            Value = (object?)remoteItem.ExecutionStatus ?? DBNull.Value,
        });
        cmd.Parameters.Add(new NpgsqlParameter("lastError", NpgsqlDbType.Text)
        {
            Value = (object?)remoteItem.Error?.Message ?? DBNull.Value,
        });

        return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private async Task<(Guid? LocalAccountId, Guid? LocalCardId)> FindExistingLinkAsync(
        Guid userId,
        string pluggyAccountId,
        CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT local_account_id, local_card_id
            FROM pluggy_account_links
            WHERE user_id = @userId AND pluggy_account_id = @pluggyAccountId
            """);
        cmd.Parameters.AddWithValue("userId", userId);
        cmd.Parameters.AddWithValue("pluggyAccountId", pluggyAccountId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return (null, null);

        return (
            reader.IsDBNull(0) ? null : reader.GetGuid(0),
            reader.IsDBNull(1) ? null : reader.GetGuid(1));
    }

    private async Task UpsertAccountLinkAsync(
        Guid userId,
        Guid itemRowId,
        PluggyRemoteAccount remoteAcc,
        string displayName,
        string? maskedNumber,
        long remoteBalanceCents,
        Guid? localAccountId,
        Guid? localCardId,
        CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO pluggy_account_links (
                id, user_id, pluggy_item_row_id, pluggy_account_id,
                pluggy_type, pluggy_subtype, name, masked_number, currency_code,
                remote_balance_cents, local_account_id, local_card_id, last_synced_at
            )
            VALUES (
                @id, @userId, @itemRowId, @pluggyAccountId,
                @type, @subtype, @name, @maskedNumber, @currency,
                @balance, @localAccountId, @localCardId, NOW()
            )
            ON CONFLICT (user_id, pluggy_account_id) DO UPDATE SET
                pluggy_item_row_id = EXCLUDED.pluggy_item_row_id,
                name = EXCLUDED.name,
                masked_number = EXCLUDED.masked_number,
                remote_balance_cents = EXCLUDED.remote_balance_cents,
                local_account_id = COALESCE(EXCLUDED.local_account_id, pluggy_account_links.local_account_id),
                local_card_id = COALESCE(EXCLUDED.local_card_id, pluggy_account_links.local_card_id),
                last_synced_at = NOW()
            """);

        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("userId", userId);
        cmd.Parameters.AddWithValue("itemRowId", itemRowId);
        cmd.Parameters.AddWithValue("pluggyAccountId", remoteAcc.Id);
        cmd.Parameters.AddWithValue("type", remoteAcc.Type ?? "BANK");
        cmd.Parameters.AddWithValue("subtype", remoteAcc.Subtype ?? string.Empty);
        cmd.Parameters.AddWithValue("name", Truncate(displayName, 80));
        cmd.Parameters.Add(new NpgsqlParameter("maskedNumber", NpgsqlDbType.Text)
        {
            Value = (object?)maskedNumber ?? DBNull.Value,
        });
        cmd.Parameters.AddWithValue("currency", remoteAcc.CurrencyCode ?? "BRL");
        cmd.Parameters.AddWithValue("balance", remoteBalanceCents);
        cmd.Parameters.Add(new NpgsqlParameter("localAccountId", NpgsqlDbType.Uuid)
        {
            Value = (object?)localAccountId ?? DBNull.Value,
        });
        cmd.Parameters.Add(new NpgsqlParameter("localCardId", NpgsqlDbType.Uuid)
        {
            Value = (object?)localCardId ?? DBNull.Value,
        });

        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------- Mapeamentos e sanitização ----------

    private static long ToCents(decimal value) => (long)Math.Round(value * 100m, MidpointRounding.AwayFromZero);

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength];

    private static string BuildDisplayName(string connectorName, string? accountName, string fallback)
    {
        var clean = accountName?.Trim();
        if (string.IsNullOrWhiteSpace(clean))
            return Truncate($"{connectorName} {fallback}", 80);

        return clean.Contains(connectorName, StringComparison.OrdinalIgnoreCase)
            ? Truncate(clean, 80)
            : Truncate($"{connectorName} · {clean}", 80);
    }

    private static string? ExtractLast4(string? number)
    {
        if (string.IsNullOrWhiteSpace(number))
            return null;

        var match = TrailingDigits.Match(number.Trim());
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? MaskAccountNumber(string? number)
    {
        var last4 = ExtractLast4(number);
        return last4 is not null ? $"•••• {last4}" : null;
    }

    private static string MapAccountKind(string? subtype) => subtype?.ToUpperInvariant() switch
    {
        "CHECKING_ACCOUNT" => "checking",
        "SAVINGS_ACCOUNT" => "savings",
        "INVESTMENT" => "investment",
        _ => "checking",
    };

    private static string MapCardBrand(string? brand) => brand?.ToUpperInvariant() switch
    {
        "VISA" => "visa",
        "MASTERCARD" => "mastercard",
        "ELO" => "elo",
        "AMEX" or "AMERICAN EXPRESS" => "amex",
        "HIPERCARD" => "hipercard",
        _ => "other",
    };

    private static string NormalizeHexColor(string? raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        var clean = raw.Trim().TrimStart('#');
        if (clean.Length == 6 && clean.All(Uri.IsHexDigit))
            return "#" + clean;

        return fallback;
    }

    private static bool IsCardPaymentDescription(string description)
    {
        var lower = description.ToLowerInvariant();
        return lower.Contains("pagamento de fatura")
            || lower.Contains("pagamento fatura")
            || lower.Contains("pagto fatura")
            || lower.Contains("pagamento recebido")
            || lower.Contains("fatura paga");
    }

    private static Guid? MatchCategory(
        IReadOnlyList<CategoryDto> categories,
        string kind,
        string? pluggyCategory,
        string description)
    {
        var text = $"{pluggyCategory} {description}".ToLowerInvariant();
        string? targetName = null;

        if (kind == "income")
        {
            if (text.Contains("salary") || text.Contains("salário") || text.Contains("salario") || text.Contains("folha"))
                targetName = "Salário";
            else if (text.Contains("investment") || text.Contains("rendimento") || text.Contains("dividendo") || text.Contains("juros"))
                targetName = "Investimentos";
            else if (text.Contains("freelance"))
                targetName = "Freelance";
        }
        else
        {
            if (text.Contains("food") || text.Contains("grocer") || text.Contains("restaurant") || text.Contains("ifood") || text.Contains("mercado") || text.Contains("padaria"))
                targetName = "Alimentação";
            else if (text.Contains("transport") || text.Contains("uber") || text.Contains("99") || text.Contains("combust") || text.Contains("posto") || text.Contains("estacionamento"))
                targetName = "Transporte";
            else if (text.Contains("health") || text.Contains("pharm") || text.Contains("drogaria") || text.Contains("farmacia") || text.Contains("farmácia") || text.Contains("médic"))
                targetName = "Saúde";
            else if (text.Contains("housing") || text.Contains("rent") || text.Contains("aluguel") || text.Contains("condom"))
                targetName = "Moradia";
            else if (text.Contains("education") || text.Contains("curso") || text.Contains("faculdade") || text.Contains("livro"))
                targetName = "Educação";
            else if (text.Contains("entertain") || text.Contains("leisure") || text.Contains("cinema") || text.Contains("netflix") || text.Contains("spotify") || text.Contains("steam"))
                targetName = "Lazer";
            else if (text.Contains("bill") || text.Contains("utilit") || text.Contains("energia") || text.Contains("internet") || text.Contains("telefone"))
                targetName = "Contas e serviços";
            else if (text.Contains("shop") || text.Contains("amazon") || text.Contains("mercado livre") || text.Contains("loja"))
                targetName = "Compras";
        }

        if (targetName is null)
            return null;

        return categories.FirstOrDefault(c =>
            c.Kind == kind && string.Equals(c.Name, targetName, StringComparison.OrdinalIgnoreCase))?.Id;
    }
}
