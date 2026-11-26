using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PersonalControl.Api.Finance.Ofx;

namespace PersonalControl.Api.Finance;

// Importação de extratos OFX em duas etapas:
//   1. POST /transactions/import/ofx/preview  (multipart, campo "file") -> lê o arquivo e devolve os lançamentos
//   2. POST /transactions/import/ofx          (JSON) -> grava os lançamentos escolhidos na conta
// Cada lançamento carrega um ExternalId (FITID do banco); reimportar o mesmo extrato não duplica nada.
public static partial class OfxImportEndpoints
{
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private const int MaxTransactionsPerImport = 2000;

    public static void MapOfxImport(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/transactions/import/ofx").WithTags("Transactions").RequireAuthorization();

        group.MapPost("/preview", async (
            IFormFile? file,
            Guid? accountId,
            ClaimsPrincipal principal,
            IFinanceStore store,
            IRecurringStore recurringStore,
            CancellationToken ct) =>
        {
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "Envie um arquivo OFX ou CSV." });

            if (file.Length > MaxFileBytes)
                return Results.BadRequest(new { error = "O arquivo pode ter no máximo 5 MB." });

            var userId = principal.GetUserId();
            if (accountId is not null && !await store.AccountBelongsToAsync(userId, accountId.Value, ct))
                return Results.BadRequest(new { error = "Conta não encontrada." });

            var isCsv = file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);

            IReadOnlyList<OfxStatement> statements;
            try
            {
                await using var stream = file.OpenReadStream();
                statements = isCsv
                    ? await CsvStatementParser.ParseAsync(stream, ct)
                    : await OfxParser.ParseAsync(stream, ct);
            }
            catch (OfxParseException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            var items = BuildPreviewItems(statements);

            // Auto-categorização inteligente baseada no histórico de lançamentos do próprio usuário.
            if (items.Count > 0)
            {
                var expenseHints = await recurringStore.GetCategorySuggestionsAsync(userId, "expense", ct);
                var incomeHints = await recurringStore.GetCategorySuggestionsAsync(userId, "income", ct);

                items = items.Select(item =>
                {
                    var hints = item.Type == "income" ? incomeHints : expenseHints;
                    var suggested = SuggestCategory(item.Description, hints);
                    return suggested.HasValue ? item with { SuggestedCategoryId = suggested } : item;
                }).ToList();
            }

            if (accountId is not null && items.Count > 0)
            {
                var existing = await store.ListExternalIdsAsync(
                    userId, accountId.Value, items.Select(i => i.ExternalId).ToArray(), ct);
                items = items.Select(i => i with { AlreadyImported = existing.Contains(i.ExternalId) }).ToList();
            }

            var first = statements[0];
            return Results.Ok(new OfxPreviewDto(
                BankId: first.BankId,
                AccountNumber: first.AccountId,
                Currency: first.Currency,
                StartDate: FormatDate(statements.Min(s => s.StartDate)),
                EndDate: FormatDate(statements.Max(s => s.EndDate)),
                LedgerBalanceCents: first.LedgerBalanceCents,
                Transactions: items));
        })
        .DisableAntiforgery(); // API autenticada por token JWT, sem cookies: não há risco de CSRF.

        group.MapPost("", async (OfxImportRequest request, ClaimsPrincipal principal, IFinanceStore store, CancellationToken ct) =>
        {
            var userId = principal.GetUserId();
            var error = await ValidateImportAsync(request, userId, store, ct);
            if (error is not null)
                return Results.BadRequest(new { error });

            var items = request.Transactions
                .Select(t => t with { Description = (t.Description ?? string.Empty).Trim() })
                .ToList();

            var imported = await store.ImportTransactionsAsync(userId, request.AccountId, items, ct);
            return Results.Ok(new OfxImportResultDto(imported, items.Count - imported));
        });
    }

    // ---------- Pré-visualização ----------

    private static List<OfxPreviewTransactionDto> BuildPreviewItems(IReadOnlyList<OfxStatement> statements)
    {
        var items = new List<OfxPreviewTransactionDto>();
        var usedIds = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var transaction in statements.SelectMany(s => s.Transactions))
        {
            // Lançamentos de valor zero (ex.: informativos) não entram.
            if (transaction.AmountCents == 0)
                continue;

            var description = BuildDescription(transaction.Name, transaction.Memo);
            var externalId = UniqueId(BaseExternalId(transaction, description), usedIds);

            items.Add(new OfxPreviewTransactionDto(
                ExternalId: externalId,
                Type: transaction.AmountCents > 0 ? "income" : "expense",
                AmountCents: Math.Abs(transaction.AmountCents),
                Description: description,
                OccurredOn: FormatDate(transaction.PostedOn)!,
                OfxType: transaction.TransactionType,
                AlreadyImported: false));
        }

        return items
            .OrderBy(i => i.OccurredOn, StringComparer.Ordinal)
            .ToList();
    }

    // FITID do banco; se ausente, um hash estável de data + valor + descrição.
    private static string BaseExternalId(OfxTransaction transaction, string description)
    {
        var fitId = transaction.FitId.Trim();
        if (fitId.Length > 0)
            return fitId.Length <= 180 ? fitId : "fitid:" + Hash(fitId);

        var source = $"{transaction.PostedOn:yyyyMMdd}|{transaction.AmountCents}|{description}";
        return "hash:" + Hash(source);
    }

    // Alguns bancos repetem o FITID dentro do mesmo arquivo; acrescenta um sufixo para diferenciar.
    private static string UniqueId(string baseId, Dictionary<string, int> used)
    {
        var count = used.GetValueOrDefault(baseId) + 1;
        used[baseId] = count;
        return count == 1 ? baseId : $"{baseId}#{count}";
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];

    // Junta NAME e MEMO sem repetir texto, normaliza espaços e limita a 200 caracteres.
    private static string BuildDescription(string? name, string? memo)
    {
        var cleanName = Normalize(name);
        var cleanMemo = Normalize(memo);

        string description;
        if (cleanName.Length == 0)
            description = cleanMemo;
        else if (cleanMemo.Length == 0 || cleanName.Contains(cleanMemo, StringComparison.OrdinalIgnoreCase))
            description = cleanName;
        else if (cleanMemo.Contains(cleanName, StringComparison.OrdinalIgnoreCase))
            description = cleanMemo;
        else
            description = $"{cleanName} - {cleanMemo}";

        return description.Length <= 200 ? description : description[..200].TrimEnd();
    }

    private static string Normalize(string? value) =>
        value is null ? string.Empty : Whitespace().Replace(value, " ").Trim();

    private static Guid? SuggestCategory(string description, Dictionary<string, Guid> hints)
    {
        if (hints.Count == 0 || string.IsNullOrWhiteSpace(description))
            return null;

        var norm = description.Trim().ToLowerInvariant();
        if (hints.TryGetValue(norm, out var exact))
            return exact;

        // Tenta casar pelo primeiro termo significativo (>= 4 caracteres, ex.: "uber", "ifood", "spotify").
        var firstWord = norm.Split([' ', '-', '*', '/'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (firstWord is not null && firstWord.Length >= 4)
        {
            foreach (var (key, catId) in hints)
            {
                if (key.Contains(firstWord, StringComparison.OrdinalIgnoreCase))
                    return catId;
            }
        }

        return null;
    }

    private static string? FormatDate(DateOnly? date) =>
        date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // ---------- Importação ----------

    private static async Task<string?> ValidateImportAsync(
        OfxImportRequest request,
        Guid userId,
        IFinanceStore store,
        CancellationToken ct)
    {
        if (request.Transactions is null || request.Transactions.Count == 0)
            return "Selecione ao menos um lançamento para importar.";

        if (request.Transactions.Count > MaxTransactionsPerImport)
            return $"Importe no máximo {MaxTransactionsPerImport} lançamentos por vez.";

        if (!await store.AccountBelongsToAsync(userId, request.AccountId, ct))
            return "Conta não encontrada.";

        var categoryKinds = (await store.ListCategoriesAsync(userId, ct)).ToDictionary(c => c.Id, c => c.Kind);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < request.Transactions.Count; index++)
        {
            var item = request.Transactions[index];
            var line = $"Lançamento {index + 1}";

            if (string.IsNullOrWhiteSpace(item.ExternalId) || item.ExternalId.Length > 200)
                return $"{line}: identificador do banco inválido.";

            if (!seenIds.Add(item.ExternalId))
                return $"{line}: lançamento repetido na importação.";

            if (item.Type is not ("income" or "expense"))
                return $"{line}: tipo inválido.";

            if (item.AmountCents <= 0 || item.AmountCents > FinanceEndpoints.MaxAmountCents)
                return $"{line}: o valor precisa ser maior que zero.";

            if (!DateOnly.TryParseExact(item.OccurredOn, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                return $"{line}: data inválida.";

            if ((item.Description ?? string.Empty).Trim().Length > 200)
                return $"{line}: a descrição pode ter no máximo 200 caracteres.";

            if (item.CategoryId is not null)
            {
                if (!categoryKinds.TryGetValue(item.CategoryId.Value, out var kind))
                    return $"{line}: categoria não encontrada.";

                if (kind != item.Type)
                    return $"{line}: a categoria não corresponde ao tipo do lançamento.";
            }
        }

        return null;
    }
}
