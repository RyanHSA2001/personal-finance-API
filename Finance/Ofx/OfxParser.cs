using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PersonalControl.Api.Finance.Ofx;

// Lançamento como veio do banco. AmountCents tem sinal: positivo = crédito, negativo = débito.
public record OfxTransaction(
    string FitId,
    string TransactionType,
    DateOnly PostedOn,
    long AmountCents,
    string? Name,
    string? Memo,
    string? CheckNumber);

// Um extrato (conta corrente/poupança em STMTRS ou cartão de crédito em CCSTMTRS).
public record OfxStatement(
    string? BankId,
    string? AccountId,
    string? AccountType,
    string? Currency,
    DateOnly? StartDate,
    DateOnly? EndDate,
    long? LedgerBalanceCents,
    DateOnly? LedgerBalanceDate,
    IReadOnlyList<OfxTransaction> Transactions);

public class OfxParseException(string message) : Exception(message);

// Leitor de arquivos OFX. Aceita as duas variantes do formato:
//   - OFX 1.x (SGML): tags de valor sem fechamento, ex.: "<TRNAMT>-20.00"
//   - OFX 2.x (XML):  tags de valor fechadas, ex.: "<TRNAMT>-20.00</TRNAMT>"
// Só os blocos agregados (STMTTRN, BANKTRANLIST...) precisam de fechamento, o que vale para ambas.
public static partial class OfxParser
{
    static OfxParser()
    {
        // Habilita o Windows-1252 (CHARSET:1252), padrão dos bancos brasileiros.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static async Task<IReadOnlyList<OfxStatement>> ParseAsync(Stream stream, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return Parse(buffer.ToArray());
    }

    public static IReadOnlyList<OfxStatement> Parse(byte[] content) => ParseText(Decode(content));

    public static IReadOnlyList<OfxStatement> ParseText(string text)
    {
        var start = text.IndexOf("<OFX>", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            throw new OfxParseException("O arquivo não parece ser um OFX válido.");

        var body = text[start..];
        var statements = new List<OfxStatement>();

        foreach (var block in Aggregates(body, "STMTRS").Concat(Aggregates(body, "CCSTMTRS")))
            statements.Add(ParseStatement(block));

        if (statements.Count == 0)
            throw new OfxParseException("Nenhum extrato bancário encontrado no arquivo OFX.");

        return statements;
    }

    private static OfxStatement ParseStatement(string block)
    {
        var account = Aggregate(block, "BANKACCTFROM") ?? Aggregate(block, "CCACCTFROM") ?? string.Empty;
        var transactionList = Aggregate(block, "BANKTRANLIST") ?? string.Empty;
        var ledger = Aggregate(block, "LEDGERBAL");

        var transactions = Aggregates(transactionList, "STMTTRN")
            .Select(ParseTransaction)
            .ToList();

        return new OfxStatement(
            BankId: Leaf(account, "BANKID"),
            AccountId: Leaf(account, "ACCTID"),
            AccountType: Leaf(account, "ACCTTYPE"),
            Currency: Leaf(block, "CURDEF"),
            StartDate: ParseOptionalDate(Leaf(transactionList, "DTSTART")),
            EndDate: ParseOptionalDate(Leaf(transactionList, "DTEND")),
            LedgerBalanceCents: ledger is null ? null : ParseOptionalAmount(Leaf(ledger, "BALAMT")),
            LedgerBalanceDate: ledger is null ? null : ParseOptionalDate(Leaf(ledger, "DTASOF")),
            Transactions: transactions);
    }

    private static OfxTransaction ParseTransaction(string block)
    {
        var posted = Leaf(block, "DTPOSTED")
            ?? throw new OfxParseException("Lançamento sem data (DTPOSTED) no arquivo OFX.");
        var amount = Leaf(block, "TRNAMT")
            ?? throw new OfxParseException("Lançamento sem valor (TRNAMT) no arquivo OFX.");

        return new OfxTransaction(
            FitId: Leaf(block, "FITID") ?? string.Empty,
            TransactionType: (Leaf(block, "TRNTYPE") ?? "OTHER").ToUpperInvariant(),
            PostedOn: ParseDate(posted),
            AmountCents: ParseAmount(amount),
            Name: Leaf(block, "NAME"),
            Memo: Leaf(block, "MEMO"),
            CheckNumber: Leaf(block, "CHECKNUM"));
    }

    // ---------- Conversões ----------

    // Datas OFX: AAAAMMDD[HHMMSS[.XXX]][[-3:BRT]]. Usa apenas a parte da data, no fuso informado pelo banco.
    internal static DateOnly ParseDate(string value)
    {
        var digits = value.Trim();
        if (digits.Length < 8
            || !DateOnly.TryParseExact(digits[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new OfxParseException($"Data inválida no arquivo OFX: \"{value}\".");
        }

        return date;
    }

    // Valores: "-20.00", "2053.74", "+10", e variantes com vírgula ("-20,00", "1.234,56").
    internal static long ParseAmount(string value)
    {
        var text = value.Trim().Replace(" ", string.Empty);

        if (text.Contains(',') && text.Contains('.'))
            text = text.Replace(".", string.Empty).Replace(',', '.');
        else if (text.Contains(','))
            text = text.Replace(',', '.');

        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var amount))
        {
            throw new OfxParseException($"Valor inválido no arquivo OFX: \"{value}\".");
        }

        return (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);
    }

    private static DateOnly? ParseOptionalDate(string? value) => value is null ? null : ParseDate(value);

    private static long? ParseOptionalAmount(string? value) => value is null ? null : ParseAmount(value);

    // ---------- Leitura das tags ----------

    // Conteúdo do primeiro bloco <TAG>...</TAG>.
    private static string? Aggregate(string input, string tag) =>
        Aggregates(input, tag).FirstOrDefault();

    private static IEnumerable<string> Aggregates(string input, string tag)
    {
        var pattern = $@"<{tag}>(.*?)</{tag}>";
        foreach (Match match in Regex.Matches(input, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline))
            yield return match.Groups[1].Value;
    }

    // Valor de uma tag simples: o texto após <TAG> até o próximo "<" ou quebra de linha.
    private static string? Leaf(string input, string tag)
    {
        var match = Regex.Match(input, $@"<{tag}>([^<\r\n]*)", RegexOptions.IgnoreCase);
        if (!match.Success)
            return null;

        var value = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
        return value.Length == 0 ? null : value;
    }

    // ---------- Codificação ----------

    // Tenta UTF-8 estrito primeiro (alguns bancos declaram 1252 mas enviam UTF-8);
    // se houver bytes inválidos, usa Windows-1252.
    private static string Decode(byte[] content)
    {
        var span = content.AsSpan();
        if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            span = span[3..];

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(span);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1252).GetString(span);
        }
    }
}
