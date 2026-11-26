using System.Globalization;
using System.Text;

namespace PersonalControl.Api.Finance.Ofx;

// Leitor flexível de extratos CSV de bancos brasileiros (Nubank, Inter, Itaú, Bradesco, Santander, BB, etc.).
// Detecta automaticamente separador (; ou ,), cabeçalhos em PT/EN, datas DD/MM/AAAA ou AAAA-MM-DD e valores BR/US.
public static class CsvStatementParser
{
    public static async Task<IReadOnlyList<OfxStatement>> ParseAsync(Stream stream, CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var content = await reader.ReadToEndAsync(ct);

        var rawLines = content
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (rawLines.Length < 2)
            throw new OfxParseException("O arquivo CSV precisa ter cabeçalho e ao menos um lançamento.");

        // Localiza a linha de cabeçalho (alguns bancos colocam metadados nas primeiras linhas).
        var headerIndex = -1;
        char delimiter = ',';
        string[] headerCols = [];

        for (var i = 0; i < Math.Min(rawLines.Length, 12); i++)
        {
            var sep = DetectDelimiter(rawLines[i]);
            var cols = SplitCsvLine(rawLines[i], sep);
            if (cols.Length >= 3 && HasDateAndAmountHeader(cols))
            {
                headerIndex = i;
                delimiter = sep;
                headerCols = cols;
                break;
            }
        }

        if (headerIndex < 0)
            throw new OfxParseException("Não foi possível identificar as colunas de Data e Valor no arquivo CSV.");

        var dateCol = FindColumn(headerCols, ["data", "date", "dt", "lancamento", "lançamento"]);
        var amountCol = FindColumn(headerCols, ["valor", "amount", "value", "quantia"]);
        var descCol = FindColumn(headerCols, ["descricao", "descrição", "historico", "histórico", "title", "titulo", "título", "estabelecimento", "memo", "description"]);
        var idCol = FindColumn(headerCols, ["identificador", "id", "fitid", "codigo", "código"]);

        if (dateCol < 0 || amountCol < 0)
            throw new OfxParseException("O CSV precisa conter colunas de Data e Valor.");

        // Detecta padrão de fatura de cartão (Nubank fatura usa "date,title,amount" onde compras são positivas).
        var isCardInvoiceCsv = headerCols.Any(c => NormalizeHeader(c) is "title" or "category")
            && !headerCols.Any(c => NormalizeHeader(c) is "identificador" or "saldo");

        var transactions = new List<OfxTransaction>();
        for (var i = headerIndex + 1; i < rawLines.Length; i++)
        {
            var cols = SplitCsvLine(rawLines[i], delimiter);
            if (cols.Length <= Math.Max(dateCol, amountCol))
                continue;

            if (!TryParseDate(cols[dateCol], out var postedOn))
                continue;

            if (!TryParseAmountCents(cols[amountCol], out var amountCents) || amountCents == 0)
                continue;

            // Em CSV de fatura de cartão (ex.: Nubank fatura), valores positivos são compras (saídas).
            if (isCardInvoiceCsv)
                amountCents = -amountCents;

            var description = descCol >= 0 && descCol < cols.Length ? cols[descCol].Trim() : string.Empty;
            var fitId = idCol >= 0 && idCol < cols.Length ? cols[idCol].Trim() : string.Empty;

            transactions.Add(new OfxTransaction(
                FitId: fitId,
                TransactionType: amountCents > 0 ? "CREDIT" : "DEBIT",
                PostedOn: postedOn,
                AmountCents: amountCents,
                Name: description,
                Memo: string.Empty,
                CheckNumber: null));
        }

        if (transactions.Count == 0)
            throw new OfxParseException("Nenhum lançamento válido foi encontrado no arquivo CSV.");

        var minDate = transactions.Min(t => t.PostedOn);
        var maxDate = transactions.Max(t => t.PostedOn);

        return
        [
            new OfxStatement(
                BankId: "CSV",
                AccountId: null,
                AccountType: null,
                Currency: "BRL",
                StartDate: minDate,
                EndDate: maxDate,
                LedgerBalanceCents: null,
                LedgerBalanceDate: null,
                Transactions: transactions)
        ];
    }

    private static char DetectDelimiter(string line)
    {
        var semicolons = line.Count(c => c == ';');
        var commas = line.Count(c => c == ',');
        return semicolons >= commas && semicolons > 0 ? ';' : ',';
    }

    private static bool HasDateAndAmountHeader(string[] cols) =>
        FindColumn(cols, ["data", "date", "dt"]) >= 0 &&
        FindColumn(cols, ["valor", "amount", "value"]) >= 0;

    private static int FindColumn(string[] headers, string[] candidates)
    {
        for (var i = 0; i < headers.Length; i++)
        {
            var norm = NormalizeHeader(headers[i]);
            if (candidates.Any(c => norm == c || norm.Contains(c, StringComparison.Ordinal)))
                return i;
        }
        return -1;
    }

    private static string NormalizeHeader(string value) =>
        value.Trim().Trim('"').ToLowerInvariant()
            .Replace("ç", "c")
            .Replace("ã", "a")
            .Replace("á", "a")
            .Replace("é", "e")
            .Replace("í", "i")
            .Replace("ó", "o")
            .Replace("ú", "u");

    private static string[] SplitCsvLine(string line, char delimiter)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (ch == delimiter && !inQuotes)
            {
                result.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        result.Add(current.ToString().Trim());
        return [.. result];
    }

    private static bool TryParseDate(string raw, out DateOnly date)
    {
        var clean = raw.Trim().Trim('"');
        if (clean.Length >= 10)
            clean = clean[..10];

        string[] formats = ["yyyy-MM-dd", "dd/MM/yyyy", "dd-MM-yyyy", "MM/dd/yyyy"];
        return DateOnly.TryParseExact(clean, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static bool TryParseAmountCents(string raw, out long cents)
    {
        cents = 0;
        var clean = raw.Trim().Trim('"')
            .Replace("R$", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" ", "");

        if (clean.Length == 0)
            return false;

        var hasComma = clean.Contains(',');
        var hasDot = clean.Contains('.');

        if (hasComma && hasDot)
        {
            // Se a última ocorrência for vírgula, formato BR (1.234,56); senão US (1,234.56).
            clean = clean.LastIndexOf(',') > clean.LastIndexOf('.')
                ? clean.Replace(".", "").Replace(',', '.')
                : clean.Replace(",", "");
        }
        else if (hasComma)
        {
            clean = clean.Replace(',', '.');
        }

        if (!decimal.TryParse(clean, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
            return false;

        cents = (long)Math.Round(value * 100m, MidpointRounding.AwayFromZero);
        return true;
    }
}
