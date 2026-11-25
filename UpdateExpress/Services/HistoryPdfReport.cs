using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UpdateExpress.Models;

namespace UpdateExpress.Services;

// Standalone A4 PDF using the built-in PDF WinAnsi Helvetica font.
// WinAnsi preserves Norwegian æ, ø, å without a NuGet dependency.
public static class HistoryPdfReport
{
    private static bool IsSuccess(string? value) =>
        Has(value, "success", "succeeded", "vellykket", "installed", "completed");
    private static bool IsFailure(string? value) =>
        Has(value, "fail", "mislykket", "error", "feilet");
    private static bool Has(string? value, params string[] terms) =>
        terms.Any(t => (value ?? "").Contains(t, StringComparison.OrdinalIgnoreCase));

    // Keep only characters supported by PDF WinAnsiEncoding (Windows-1252).
    // Text is stored as literal strings; escapes are applied after conversion.
    private static string Safe(string? value)
    {
        var b = new StringBuilder();
        foreach (var c in value ?? "")
        {
            if (c is '\r' or '\n' or '\t') { b.Append(' '); continue; }
            if (c < 32) continue;
            b.Append(c switch
            {
                '–' or '—' => '-', '‘' or '’' => '\'', '“' or '”' => '"',
                '•' => '-', '…' => '.', '€' => 'E',
                _ when c <= 255 => c,
                _ => '?'
            });
        }
        return b.ToString();
    }
    private static string PdfLiteral(string value) => Safe(value)
        .Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    private static IEnumerable<string> Wrap(string? text, int max = 96)
    {
        var remaining = Safe(text).Trim();
        if (remaining.Length == 0) { yield return "Not available"; yield break; }
        while (remaining.Length > max)
        {
            int breakAt = remaining.LastIndexOf(' ', max);
            if (breakAt < max / 2) breakAt = max;
            yield return remaining[..breakAt].TrimEnd();
            remaining = remaining[breakAt..].TrimStart();
        }
        if (remaining.Length > 0) yield return remaining;
    }

    private readonly record struct PdfLine(string Text, bool Bold);

    public static void Write(string filename, IReadOnlyList<UpdateEntry> entries, string scope)
    {
        const int maxLines = 53;
        var pages = new List<List<PdfLine>>();
        var lines = new List<PdfLine>();
        void Add(string line, bool bold = false)
        {
            if (lines.Count >= maxLines) { pages.Add(lines); lines = new List<PdfLine>(); }
            lines.Add(new PdfLine(line, bold));
        }
        Add("MicroSystems | Update Express - Installation History Report");
        Add($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz} | Computer: {Environment.MachineName}");
        Add($"User: {Environment.UserDomainName}\\{Environment.UserName} | Scope: {scope}");
        Add($"Events: {entries.Count} | Success: {entries.Count(e => IsSuccess(e.Status))} | Failed: {entries.Count(e => IsFailure(e.Status))} | Fixed: {entries.Count(e => e.IsHistoryFixed)}");
        Add("Source: Windows Update Agent history, plus events Update Express saved before a Windows Update fix.");
        Add("A matching KB in later events does not prove a previous failure was repaired.");
        Add("History events do not verify current installation or uninstall availability.");
        Add(new string('-', 96));
        int i = 0;
        foreach (var e in entries)
        {
            // Keep an event together when possible rather than splitting it at the page edge.
            var titleLines = Wrap(e.Title, 92).ToList();
            int needed = 4 + titleLines.Count;
            if (lines.Count > 0 && lines.Count + needed > maxLines)
            { pages.Add(lines); lines = new List<PdfLine>(); }
            Add($"{++i}. {Safe(e.DateShown)} | {Safe(e.KB)} | {Safe(HistoryStatusDisplay.Format(e.Status))}");
            foreach (var title in titleLines) Add("   " + title);
            var category = Safe(e.Category);
            if (string.IsNullOrWhiteSpace(category)) category = "Not available";
            var error = Safe(e.ErrorCode);
            if (string.IsNullOrWhiteSpace(error)) error = "none";
            Add($"   Category: {category} | Error: {error}");
            Add($"   Source: {Safe(e.Source)}");
            Add("");
        }
        int failed = entries.Count(e => e.IsHistoryFailed);
        string closed = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz");
        if (lines.Count > 0 && lines.Count + 3 > maxLines)
        { pages.Add(lines); lines = new List<PdfLine>(); }
        Add("");
        if (entries.Count == 0)
        {
            Add("This report does not confirm that this computer is up to date.", true);
            Add($"Report closed {closed}. No history events were included.", true);
        }
        else if (failed == 0)
        {
            Add("This computer is up to date.", true);
            Add($"Report closed {closed}. No failed events in this history.", true);
        }
        else
        {
            Add("This computer is not confirmed up to date.", true);
            Add($"Report closed {closed}. Failed events in this history: {failed}.", true);
        }
        if (lines.Count > 0) pages.Add(lines);
        if (pages.Count == 0) pages.Add(new List<PdfLine> { new("No history events.", false) });

        // PDF objects are written as bytes; offsets and stream lengths are exact.
        var objects = new List<byte[]> { Array.Empty<byte>(), Array.Empty<byte>() };
        objects.Add(Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));
        objects.Add(Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"));
        var kids = new List<int>();
        foreach (var page in pages)
        {
            var content = new StringBuilder("BT /F1 9 Tf 45 800 Td 13 TL\n");
            foreach (var line in page)
            {
                content.Append(line.Bold ? "/F2 11 Tf 16 TL\n" : "/F1 9 Tf 13 TL\n");
                content.Append('(').Append(PdfLiteral(line.Text)).Append(") Tj T*\n");
            }
            content.Append("ET\n");
            var contentBytes = Encoding.Latin1.GetBytes(content.ToString());
            int pageId = objects.Count + 1;
            int streamId = pageId + 1;
            kids.Add(pageId);
            objects.Add(Encoding.ASCII.GetBytes($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {streamId} 0 R >>"));
            using var buffer = new MemoryStream();
            void Put(string t) { var bytes = Encoding.ASCII.GetBytes(t); buffer.Write(bytes); }
            Put($"<< /Length {contentBytes.Length} >>\nstream\n");
            buffer.Write(contentBytes);
            Put("endstream");
            objects.Add(buffer.ToArray());
        }
        objects[0] = Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>");
        objects[1] = Encoding.ASCII.GetBytes($"<< /Type /Pages /Kids [{string.Join(' ', kids.Select(id => $"{id} 0 R"))}] /Count {kids.Count} >>");
        using var file = new FileStream(filename, FileMode.Create, FileAccess.Write, FileShare.None);
        void W(string value) { var bytes = Encoding.ASCII.GetBytes(value); file.Write(bytes); }
        W("%PDF-1.4\n");
        var offsets = new List<long>();
        for (int n = 0; n < objects.Count; n++)
        {
            offsets.Add(file.Position);
            W($"{n + 1} 0 obj\n");
            file.Write(objects[n]);
            W("\nendobj\n");
        }
        long xref = file.Position;
        W($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) W($"{offset:0000000000} 00000 n \n");
        W($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }
}
