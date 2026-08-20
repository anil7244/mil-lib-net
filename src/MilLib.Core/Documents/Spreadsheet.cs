using System.Text;
using MilLib.Core.Data;

namespace MilLib.Core.Documents;

/// <summary>
/// A report as a file a spreadsheet will open.
///
/// Because half of what a report is for is being worked on afterwards — sorted
/// a different way, filtered, pasted into a return somebody upstairs wants in
/// their own format. A PDF is the record; this is the working copy.
/// </summary>
public static class Spreadsheet
{
    public static string From(Report report)
    {
        // A byte-order mark, because Excel on a Windows machine assumes the
        // local codepage without one — and then a Hindi title, or a rupee sign,
        // comes out as mojibake. Every title in this library is one or the
        // other.
        var text = new StringBuilder("﻿");

        text.AppendLine(Line(report.Columns));

        foreach (var row in report.Rows)
        {
            text.AppendLine(Line(row));
        }

        return text.ToString();
    }

    private static string Line(IReadOnlyList<string> cells) =>
        string.Join(",", cells.Select(Quote));

    /// <summary>
    /// Quoted whenever it could be read wrongly otherwise. A book called
    /// "Wellington, Waterloo" is one field, not two, and a doubled quote is how
    /// a quotation mark inside a field is spelt.
    /// </summary>
    private static string Quote(string value)
    {
        if (value.Length == 0)
        {
            return "";
        }

        var needs = value.Contains(',') || value.Contains('"')
            || value.Contains('\n') || value.Contains('\r')
            || value[0] == ' ' || value[^1] == ' ';

        return needs ? '"' + value.Replace("\"", "\"\"") + '"' : value;
    }
}
