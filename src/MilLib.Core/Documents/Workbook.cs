using ClosedXML.Excel;

namespace MilLib.Core.Documents;

/// <summary>
/// One sheet's worth of data, in and out of the application: a name, a row of
/// headings, and the rows under them, all as plain text.
///
/// Text on purpose. What leaves here is opened, sorted and edited in a
/// spreadsheet by somebody who does not know or care what a database column's
/// type is, and what comes back has been typed by hand — a date written the way
/// a person writes a date, a number with a stray space. The columns that mean
/// something are worked out when the rows are read, not imposed on the file.
/// </summary>
public sealed record Sheet(string Name, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>
/// Real Excel files, written and read without Excel itself — because this ships
/// air-gapped to machines that have a spreadsheet but no guarantee of which one,
/// and a .xlsx opens in every one of them and in the free viewers besides.
/// </summary>
public static class Workbook
{
    /// <summary>Turn one or more sheets into the bytes of an .xlsx file.</summary>
    public static byte[] Write(params Sheet[] sheets)
    {
        using var workbook = new XLWorkbook();

        foreach (var sheet in sheets)
        {
            var ws = workbook.Worksheets.Add(Safe(sheet.Name));

            for (var c = 0; c < sheet.Headers.Count; c++)
            {
                ws.Cell(1, c + 1).Value = sheet.Headers[c];
            }

            // The heading row set apart and pinned, so a long list still shows
            // which column is which once it is scrolled.
            var head = ws.Row(1);
            head.Style.Font.Bold = true;
            head.Style.Fill.BackgroundColor = XLColor.FromArgb(0xEE, 0xED, 0xE4);
            ws.SheetView.FreezeRows(1);

            for (var r = 0; r < sheet.Rows.Count; r++)
            {
                var row = sheet.Rows[r];

                for (var c = 0; c < row.Count; c++)
                {
                    // As text, always. An accession number, a phone number and a
                    // rank all lose to a spreadsheet's helpfulness otherwise —
                    // "007" becomes 7, a long number becomes 1.2E+10. Assigning a
                    // string keeps it a string; the text format stops the reader
                    // second-guessing it.
                    ws.Cell(r + 2, c + 1).Value = row[c];
                }
            }

            ws.Cells().Style.NumberFormat.Format = "@";
            ws.Columns().AdjustToContents(8d, 60d);
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        return stream.ToArray();
    }

    /// <summary>
    /// Read the first sheet of an .xlsx file: the first non-empty row is the
    /// headings, the rest are the rows. Blank trailing rows are dropped.
    /// </summary>
    public static Sheet Read(Stream stream)
    {
        using var workbook = new XLWorkbook(stream);

        var ws = workbook.Worksheets.First();
        var range = ws.RangeUsed();

        if (range is null)
        {
            return new Sheet(ws.Name, [], []);
        }

        var rows = range.RowsUsed().ToList();

        var headers = rows[0].Cells().Select(c => c.GetString().Trim()).ToList();
        var width = headers.Count;

        var data = new List<IReadOnlyList<string>>();

        foreach (var row in rows.Skip(1))
        {
            var cells = new string[width];

            for (var c = 0; c < width; c++)
            {
                cells[c] = row.Cell(c + 1).GetString().Trim();
            }

            // A row that is blank all the way across is somebody's spacing, not
            // a record — skip it rather than trying to import nothing.
            if (cells.Any(v => v.Length > 0))
            {
                data.Add(cells);
            }
        }

        return new Sheet(ws.Name, headers, data);
    }

    /// <summary>A sheet name Excel will accept: 31 characters, none of \ / ? * [ ] :</summary>
    private static string Safe(string name)
    {
        var cleaned = new string(name.Select(ch =>
            ch is '\\' or '/' or '?' or '*' or '[' or ']' or ':' ? ' ' : ch).ToArray());

        cleaned = cleaned.Trim();

        if (cleaned.Length == 0)
        {
            cleaned = "Sheet1";
        }

        return cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }
}
