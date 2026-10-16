using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace Bitstream.Web.Presentation;

/// <summary>
/// Writes a single-sheet Excel workbook (.xlsx, Office Open XML) with no third-party library: a
/// bold, frozen, auto-filtered header row and one row per item. Cell values may be strings,
/// numbers or <see cref="DateTime"/>s (written as they are, so convert to the reader's time zone
/// first); null leaves the cell empty. Strings are written inline, so any text is safe.
/// </summary>
public static class XlsxWriter
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    // Excel's day zero for the 1900 date system (with its leap-year quirk already accounted for).
    private static readonly DateTime ExcelEpoch = new(1899, 12, 30, 0, 0, 0, DateTimeKind.Unspecified);

    /// <param name="sheetName">At most 31 characters, without : \ / ? * [ ].</param>
    /// <param name="headers">Column titles.</param>
    /// <param name="rows">One array per row, aligned with <paramref name="headers"/>.</param>
    /// <param name="columnWidths">Optional widths in characters, aligned with <paramref name="headers"/>.</param>
    public static byte[] Write(string sheetName, IReadOnlyList<string> headers, IEnumerable<object?[]> rows, IReadOnlyList<double>? columnWidths = null)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);

        using var stream = new MemoryStream();

        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>""");
            Add(zip, "_rels/.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Add(zip, "xl/workbook.xml",
                $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="{Escape(sheetName)}" sheetId="1" r:id="rId1"/></sheets><definedNames><definedName name="_xlnm._FilterDatabase" localSheetId="0" hidden="1">'{Escape(sheetName.Replace("'", "''", StringComparison.Ordinal))}'!$A$1:${ColumnName(headers.Count - 1)}$1</definedName></definedNames></workbook>""");
            Add(zip, "xl/_rels/workbook.xml.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
            // Style 0: default; 1: bold header with a light fill; 2: date and time.
            Add(zip, "xl/styles.xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="1"><numFmt numFmtId="164" formatCode="yyyy-mm-dd hh:mm"/></numFmts><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFEDE7F3"/><bgColor indexed="64"/></patternFill></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="3"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1"/><xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>""");

            var entry = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            WriteSheet(writer, headers, rows, columnWidths);
        }

        return stream.ToArray();
    }

    private static void WriteSheet(TextWriter writer, IReadOnlyList<string> headers, IEnumerable<object?[]> rows, IReadOnlyList<double>? columnWidths)
    {
        writer.Write("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        writer.Write("""<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>""");

        if (columnWidths is { Count: > 0 })
        {
            writer.Write("<cols>");
            for (var i = 0; i < columnWidths.Count; i++)
            {
                writer.Write(string.Create(CultureInfo.InvariantCulture, $"""<col min="{i + 1}" max="{i + 1}" width="{columnWidths[i]}" customWidth="1"/>"""));
            }

            writer.Write("</cols>");
        }

        writer.Write("<sheetData>");
        WriteRow(writer, 1, [.. headers], headerStyle: true);

        var rowNumber = 1;
        foreach (var row in rows)
        {
            WriteRow(writer, ++rowNumber, row, headerStyle: false);
        }

        writer.Write("</sheetData>");
        writer.Write(string.Create(CultureInfo.InvariantCulture, $"""<autoFilter ref="A1:{ColumnName(headers.Count - 1)}{Math.Max(rowNumber, 1)}"/>"""));
        writer.Write("</worksheet>");
    }

    private static void WriteRow(TextWriter writer, int rowNumber, object?[] values, bool headerStyle)
    {
        writer.Write(string.Create(CultureInfo.InvariantCulture, $"""<row r="{rowNumber}">"""));

        for (var column = 0; column < values.Length; column++)
        {
            var reference = string.Create(CultureInfo.InvariantCulture, $"{ColumnName(column)}{rowNumber}");
            var style = headerStyle ? " s=\"1\"" : string.Empty;

            switch (values[column])
            {
                case null:
                    break;
                case DateTime dateTime:
                    WriteNumber(writer, reference, " s=\"2\"", (dateTime - ExcelEpoch).TotalDays);
                    break;
                case int or long or decimal or double or float:
                    WriteNumber(writer, reference, style, Convert.ToDouble(values[column], CultureInfo.InvariantCulture));
                    break;
                default:
                    var text = Convert.ToString(values[column], CultureInfo.InvariantCulture) ?? string.Empty;
                    writer.Write($"""<c r="{reference}" t="inlineStr"{style}><is><t xml:space="preserve">{Escape(text)}</t></is></c>""");
                    break;
            }
        }

        writer.Write("</row>");
    }

    private static void WriteNumber(TextWriter writer, string reference, string style, double value) =>
        writer.Write(string.Create(CultureInfo.InvariantCulture, $"""<c r="{reference}"{style}><v>{value.ToString("R", CultureInfo.InvariantCulture)}</v></c>"""));

    /// <summary>0 → A, 25 → Z, 26 → AA.</summary>
    private static string ColumnName(int index)
    {
        var name = string.Empty;

        for (var n = index + 1; n > 0; n = (n - 1) / 26)
        {
            name = (char)('A' + ((n - 1) % 26)) + name;
        }

        return name;
    }

    /// <summary>XML-escapes and drops characters XML 1.0 cannot carry (control characters other than tab and newlines).</summary>
    private static string Escape(string value)
    {
        var clean = new StringBuilder(value.Length);

        foreach (var ch in value)
        {
            if (ch >= 0x20 || ch is '\t' or '\n' or '\r')
            {
                clean.Append(ch);
            }
        }

        return SecurityElement.Escape(clean.ToString());
    }

    private static void Add(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
