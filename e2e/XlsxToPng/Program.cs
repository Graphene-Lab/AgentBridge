// ═══════════════════════════════════════════════════════════════════════
//  XlsxToPng — deterministic XLSX → PNG renderer for the README demo.
//
//  Renders a workbook sheet as a clean PNG (A4 landscape, 150 DPI) with the
//  data table (headers with their fills, currency number formats applied) and
//  a grouped column chart of the numeric columns. Reads the workbook straight
//  from the OOXML parts (no Aspose dependency): sharedStrings are resolved,
//  simple formulas (=B2-C2, =SUM(range)) are evaluated, styles carry the
//  fills/fonts the SpreadsheetTool wrote.
//
//  Usage: XlsxToPng <input.xlsx> <output.png>
// ═══════════════════════════════════════════════════════════════════════
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: XlsxToPng <input.xlsx> <output.png>");
            return 1;
        }
        var input = Path.GetFullPath(args[0]);
        var output = Path.GetFullPath(args[1]);
        if (!File.Exists(input)) { Console.WriteLine($"input not found: {input}"); return 1; }

        var doc = XlsxDoc.Load(input);
        var sheet = doc.Sheets.FirstOrDefault();
        if (sheet == null) { Console.WriteLine("no worksheets found"); return 1; }

        using var bitmap = Renderer.Render(sheet, doc);
        bitmap.Save(output, ImageFormat.Png);
        Console.WriteLine($"PNG saved: {output} ({bitmap.Width}x{bitmap.Height})");
        return 0;
    }

    // ── Workbook model ─────────────────────────────────────────────────

    private sealed class XlsxDoc
    {
        public required List<string> SharedStrings;
        public required List<Sheet> Sheets;
        public required Styles Styles;

        public static XlsxDoc Load(string path)
        {
            using var zip = ZipFile.OpenRead(path);
            var shared = new List<string>();
            var ss = zip.GetEntry("xl/sharedStrings.xml");
            if (ss != null)
            {
                var sdoc = XDocument.Load(ss.Open());
                XNamespace m = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                shared = sdoc.Descendants(m + "si")
                    .Select(si => string.Concat(si.Descendants(m + "t").Select(t => t.Value)))
                    .ToList();
            }

            var styles = Styles.Load(zip);

            // Real worksheet names: workbook.xml + rels map r:id → "worksheets/sheetN.xml".
            var sheetNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var wb = zip.GetEntry("xl/workbook.xml");
            if (wb != null)
            {
                XNamespace m = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
                var idToTarget = new Dictionary<string, string>();
                var rels = zip.GetEntry("xl/_rels/workbook.xml.rels");
                if (rels != null)
                {
                    var rdoc = XDocument.Load(rels.Open());
                    XNamespace pr = "http://schemas.openxmlformats.org/package/2006/relationships";
                    foreach (var rel in rdoc.Descendants(pr + "Relationship"))
                        idToTarget[rel.Attribute("Id")?.Value ?? ""] = rel.Attribute("Target")?.Value ?? "";
                }
                foreach (var sh in XDocument.Load(wb.Open()).Descendants(m + "sheet"))
                {
                    var name = sh.Attribute("name")?.Value ?? "";
                    var rid = sh.Attribute(r + "id")?.Value ?? "";
                    if (idToTarget.TryGetValue(rid, out var target))
                        sheetNames["xl/" + target.TrimStart('/')] = name;
                }
            }

            var sheets = new List<Sheet>();
            foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase))
                                         .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase))
            {
                var sdoc = XDocument.Load(e.Open());
                XNamespace m = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                var cells = new Dictionary<(int R, int C), Cell>();
                int maxRow = 0, maxCol = 0;
                foreach (var row in sdoc.Descendants(m + "row"))
                {
                    var r = int.Parse(row.Attribute("r")?.Value ?? "1") - 1;
                    foreach (var c in row.Elements(m + "c"))
                    {
                        var refName = c.Attribute("r")?.Value ?? "A1";
                        var col = ColumnIndex(refName);
                        var t = (string?)c.Attribute("t");
                        var v = c.Element(m + "v");
                        string? raw = null;
                        if (t == "inlineStr")
                            raw = string.Concat(c.Descendants(m + "t").Select(x => x.Value));
                        else if (v != null)
                            raw = v.Value;
                        else if (c.Element(m + "f") != null)
                            raw = null;   // formula without cached value → computed later
                        if (raw == null && c.Element(m + "f") != null)
                            raw = "=" + c.Element(m + "f")!.Value;
                        var styleIdx = int.Parse(c.Attribute("s")?.Value ?? "0");
                        var cell = new Cell
                        {
                            Raw = raw,
                            IsShared = t == "s",
                            Formula = c.Element(m + "f") != null,
                            StyleIndex = styleIdx,
                        };
                        cells[(r, col)] = cell;
                        if (r > maxRow) maxRow = r;
                        if (col > maxCol) maxCol = col;
                    }
                }
                sheets.Add(new Sheet
                {
                    Name = sheetNames.TryGetValue(e.FullName, out var nm) ? nm : e.FullName,
                    Cells = cells,
                    MaxRow = maxRow,
                    MaxCol = maxCol,
                });
            }
            return new XlsxDoc { SharedStrings = shared, Sheets = sheets, Styles = styles };
        }
    }

    private sealed class Sheet
    {
        public required string Name;
        public required Dictionary<(int R, int C), Cell> Cells;
        public int MaxRow, MaxCol;
        public Cell? Get(int r, int c) => Cells.TryGetValue((r, c), out var cell) ? cell : null;
    }

    private sealed class Cell
    {
        public string? Raw;          // raw value or "=formula"
        public bool IsShared;        // t="s" → Raw is a sharedStrings index
        public bool Formula;
        public int StyleIndex;
    }

    private sealed class Styles
    {
        public List<string> FillColors = new();     // per style index, "#RRGGBB" or null
        public List<bool> Bold = new();
        public List<string> NumberFormats = new();  // per style index, formatCode or ""

        public static Styles Load(ZipArchive zip)
        {
            var s = new Styles();
            var entry = zip.GetEntry("xl/styles.xml");
            if (entry == null) return s;
            var doc = XDocument.Load(entry.Open());
            XNamespace m = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

            var fills = new List<string>();
            foreach (var fill in doc.Descendants(m + "fill"))
            {
                var fg = fill.Descendants(m + "fgColor").FirstOrDefault();
                var rgb = (string?)fg?.Attribute("rgb");
                fills.Add(rgb is { Length: 8 } ? "#" + rgb[2..] : "");
            }
            var fontsBold = new List<bool>();
            foreach (var font in doc.Descendants(m + "font"))
                fontsBold.Add(font.Element(m + "b") != null);

            var numFmtById = new Dictionary<int, string>();
            foreach (var nf in doc.Descendants(m + "numFmt"))
                numFmtById[int.Parse(nf.Attribute("numFmtId")!.Value)] = nf.Attribute("formatCode")?.Value ?? "";

            foreach (var xf in doc.Descendants(m + "cellXfs").Elements(m + "xf"))
            {
                var fillId = int.Parse(xf.Attribute("fillId")?.Value ?? "0");
                var fontId = int.Parse(xf.Attribute("fontId")?.Value ?? "0");
                var numFmtId = int.Parse(xf.Attribute("numFmtId")?.Value ?? "0");
                s.FillColors.Add(fillId < fills.Count ? fills[fillId] : "");
                s.Bold.Add(fontId < fontsBold.Count && fontsBold[fontId]);
                s.NumberFormats.Add(numFmtById.TryGetValue(numFmtId, out var fc) ? fc : "");
            }
            return s;
        }
    }

    private static int ColumnIndex(string refName)
    {
        var letters = new string(refName.TakeWhile(char.IsLetter).ToArray());
        int col = 0;
        foreach (var ch in letters) col = col * 26 + (ch - 'A' + 1);
        return col - 1;
    }

    // ── Value resolution + minimal formula evaluation ───────────────────

    private static string? Resolve(Cell cell, XlsxDoc doc)
    {
        if (cell == null) return null;
        if (cell.Formula && cell.Raw?.StartsWith('=') == true)
        {
            var v = Eval(cell.Raw, (r, c) => ResolveNumber(doc.Sheets[0].Get(r, c), doc));
            return v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : null;
        }
        if (cell.IsShared && int.TryParse(cell.Raw, out var idx) && idx >= 0 && idx < doc.SharedStrings.Count)
            return doc.SharedStrings[idx];
        return cell.Raw;
    }

    private static double? ResolveNumber(Cell cell, XlsxDoc doc)
    {
        var s = Resolve(cell, doc);
        return s != null && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>Tiny evaluator: numbers, cell refs, + - * / ( ), SUM(range), SUM(a,b,...).</summary>
    private static double? Eval(string formula, Func<int, int, double?> cell)
    {
        int pos = 0;
        var expr = formula.TrimStart('=');
        try
        {
            var result = ParseExpr(expr, ref pos, cell);
            return pos >= expr.Length ? result : null;
        }
        catch { return null; }
    }

    private static double? ParseExpr(string s, ref int pos, Func<int, int, double?> cell)
    {
        var left = ParseTerm(s, ref pos, cell);
        while (pos < s.Length && (s[pos] == '+' || s[pos] == '-'))
        {
            var op = s[pos++];
            var right = ParseTerm(s, ref pos, cell);
            if (left == null || right == null) return null;
            left = op == '+' ? left + right : left - right;
        }
        return left;
    }

    private static double? ParseTerm(string s, ref int pos, Func<int, int, double?> cell)
    {
        var left = ParseFactor(s, ref pos, cell);
        while (pos < s.Length && (s[pos] == '*' || s[pos] == '/'))
        {
            var op = s[pos++];
            var right = ParseFactor(s, ref pos, cell);
            if (left == null || right == null || (op == '/' && right == 0)) return null;
            left = op == '*' ? left * right : left / right;
        }
        return left;
    }

    private static double? ParseFactor(string s, ref int pos, Func<int, int, double?> cell)
    {
        while (pos < s.Length && s[pos] == ' ') pos++;
        if (pos >= s.Length) return null;
        if (s[pos] == '(')
        {
            pos++;
            var inner = ParseExpr(s, ref pos, cell);
            if (pos < s.Length && s[pos] == ')') pos++;
            return inner;
        }
        if (char.IsLetter(s[pos]))
        {
            var word = ReadWord(s, ref pos);
            if (word.Equals("SUM", StringComparison.OrdinalIgnoreCase) && pos < s.Length && s[pos] == '(')
            {
                pos++;
                double sum = 0;
                while (true)
                {
                    while (pos < s.Length && (s[pos] == ' ' || s[pos] == ',')) pos++;
                    if (pos < s.Length && s[pos] == ')') { pos++; break; }
                    var v = ParseSumArg(s, ref pos, cell);
                    if (v == null) return null;
                    sum += v.Value;
                }
                return sum;
            }
            // cell reference like B2
            var col = 0;
            foreach (var ch in word) col = col * 26 + (ch - 'A' + 1);
            col--;
            var rowDigits = ReadDigits(s, ref pos);
            if (rowDigits.Length == 0) return null;
            var row = int.Parse(rowDigits) - 1;
            return cell(row, col);
        }
        if (char.IsDigit(s[pos]) || s[pos] == '.')
        {
            var num = ReadNumber(s, ref pos);
            return double.TryParse(num, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;
        }
        return null;
    }

    private static double? ParseSumArg(string s, ref int pos, Func<int, int, double?> cell)
    {
        while (pos < s.Length && s[pos] == ' ') pos++;
        if (pos >= s.Length) return null;
        if (char.IsLetter(s[pos]))
        {
            var start = ReadCellRef(s, ref pos);
            if (pos < s.Length && s[pos] == ':')
            {
                pos++;
                var end = ReadCellRef(s, ref pos);
                if (start == null || end == null) return null;
                double sum = 0;
                for (int r = Math.Min(start.Value.Row, end.Value.Row); r <= Math.Max(start.Value.Row, end.Value.Row); r++)
                    for (int c = Math.Min(start.Value.Col, end.Value.Col); c <= Math.Max(start.Value.Col, end.Value.Col); c++)
                    {
                        var v = cell(r, c);
                        if (v.HasValue) sum += v.Value;
                    }
                return sum;
            }
            return start.HasValue ? cell(start.Value.Row, start.Value.Col) : null;
        }
        return ParseFactor(s, ref pos, cell);
    }

    /// <summary>Reads a full cell reference like "B2" (letters + digits).</summary>
    private static (int Col, int Row)? ReadCellRef(string s, ref int pos)
    {
        var letters = new StringBuilder();
        while (pos < s.Length && char.IsLetter(s[pos])) letters.Append(s[pos++]);
        var digits = new StringBuilder();
        while (pos < s.Length && char.IsDigit(s[pos])) digits.Append(s[pos++]);
        if (letters.Length == 0 || digits.Length == 0) return null;
        int col = 0;
        foreach (var ch in letters.ToString()) col = col * 26 + (ch - 'A' + 1);
        return (col - 1, int.Parse(digits.ToString()) - 1);
    }

    private static string ReadWord(string s, ref int pos)
    {
        var sb = new StringBuilder();
        while (pos < s.Length && char.IsLetter(s[pos])) sb.Append(s[pos++]);
        return sb.ToString();
    }

    private static string ReadDigits(string s, ref int pos)
    {
        var sb = new StringBuilder();
        while (pos < s.Length && char.IsDigit(s[pos])) sb.Append(s[pos++]);
        return sb.ToString();
    }

    private static string ReadNumber(string s, ref int pos)
    {
        var sb = new StringBuilder();
        while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '.')) sb.Append(s[pos++]);
        return sb.ToString();
    }

    // ── Number format application ───────────────────────────────────────

    private static string FormatNumber(double v, string? fmt)
    {
        if (string.IsNullOrWhiteSpace(fmt) || fmt == "General" || fmt == "@")
            return v.ToString(CultureInfo.InvariantCulture);
        var f = fmt.Trim();
        var first = f.IndexOfAny(new[] { '#', '0', '?' });
        if (first < 0) return f.Trim('"');
        // Numeric core runs from the FIRST to the LAST placeholder; everything before is a
        // literal prefix, everything after a literal suffix (e.g. "$#,##0.00" → "$" + core).
        var lastPlaceholder = f.LastIndexOfAny(new[] { '#', '0', '?' });
        var prefix = f[..first].Trim('"');
        var core = f[first..(lastPlaceholder + 1)];
        var suffix = f[(lastPlaceholder + 1)..].Trim('"');
        var dot = core.IndexOf('.');
        var decimals = dot >= 0 ? core[(dot + 1)..].TakeWhile(c => c is '0' or '#').Count() : 0;
        var grouping = core.Contains(',');
        var body = decimals > 0 ? v.ToString("N" + decimals, CultureInfo.InvariantCulture)
            : grouping ? v.ToString("N0", CultureInfo.InvariantCulture)
            : v.ToString("F0", CultureInfo.InvariantCulture);
        return prefix + body + suffix;
    }

    // ── Rendering ───────────────────────────────────────────────────────

    private static class Renderer
    {
        private const int Width = 1754, Height = 1240;   // A4 landscape, 150 DPI

        public static Bitmap Render(Sheet sheet, XlsxDoc doc)
        {
            var styles = doc.Styles;
            var bmp = new Bitmap(Width, Height);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.White);

            // Used range.
            var used = Enumerable.Range(0, sheet.MaxRow + 1)
                .SelectMany(r => Enumerable.Range(0, sheet.MaxCol + 1)
                    .Select(c => (R: r, C: c)))
                .Where(p => sheet.Get(p.R, p.C) != null)
                .ToList();
            if (used.Count == 0) return bmp;
            var maxR = used.Max(p => p.R);
            var maxC = used.Max(p => p.C);

            // Primary-table detection: agents sometimes duplicate the same block at different
            // offsets (set_range replayed on A1, E1, I1...). Render only the FIRST widest run
            // of contiguous non-empty columns, so the PNG shows ONE table, not the duplicates.
            var colCounts = new int[maxC + 1];
            foreach (var p in used) colCounts[p.C]++;
            var groups = new List<(int Start, int End)>();
            for (int c = 0; c <= maxC;)
            {
                if (colCounts[c] == 0) { c++; continue; }
                int s = c;
                while (c <= maxC && colCounts[c] > 0) c++;
                groups.Add((s, c - 1));
            }
            var primary = groups.OrderByDescending(g => g.End - g.Start + 1).ThenBy(g => g.Start).First();
            int cMin = primary.Start, cMax = primary.End;
            var usedRows = used.Where(p => p.C >= cMin && p.C <= cMax).ToList();
            if (usedRows.Count > 0) maxR = usedRows.Max(p => p.R);

            const int margin = 50;
            // Title.
            using (var titleFont = new Font("Segoe UI", 34, FontStyle.Bold))
            {
                g.DrawString(sheet.Name, titleFont, Brushes.Black, margin, 24);
            }

            // ── Table ──
            var tableTop = 110;
            var tableHeight = 430;
            var tableBottom = tableTop + tableHeight;
            var headerHeight = 56;
            var rowHeight = 46;
            var nCols = cMax - cMin + 1;
            var colWidth = (Width - 2 * margin) / nCols;

            // Header row.
            for (int cc = 0; cc < nCols; cc++)
            {
                var cell = sheet.Get(0, cMin + cc);
                var fill = cell != null ? styles.FillColors[cell.StyleIndex] : "";
                var rect = new Rectangle(margin + cc * colWidth, tableTop, colWidth, headerHeight);
                using var brush = fill.Length == 6 ? new SolidBrush(ColorTranslator.FromHtml("#" + fill)) : new SolidBrush(Color.FromArgb(220, 228, 240));
                g.FillRectangle(brush, rect);
                g.DrawRectangle(Pens.Silver, rect);
                var text = cell != null ? Resolve(cell, doc) ?? "" : "";
                DrawCellText(g, text, rect, styles.Bold[cell?.StyleIndex ?? 0], rightAlign: false, fontColor: DarkOn(fill));
            }

            // Data rows.
            for (int r = 1; r <= maxR; r++)
            {
                var y = tableTop + headerHeight + (r - 1) * rowHeight;
                if (y + rowHeight > tableBottom) break;
                for (int cc = 0; cc < nCols; cc++)
                {
                    var cell = sheet.Get(r, cMin + cc);
                    var rect = new Rectangle(margin + cc * colWidth, y, colWidth, rowHeight);
                    g.DrawRectangle(Pens.Silver, rect);
                    if (cell == null) continue;
                    var isNumber = IsNumeric(cell, doc);
                    var text = Resolve(cell, doc) ?? "";
                    if (isNumber && double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var dv))
                        text = FormatNumber(dv, styles.NumberFormats[cell.StyleIndex]);
                    DrawCellText(g, text, rect, styles.Bold[cell.StyleIndex], rightAlign: isNumber, fontColor: Color.Black);
                }
            }

            // ── Chart (from the primary table columns) ──
            var chart = BuildChart(sheet, doc, maxR, cMin, cMax);
            if (chart != null)
                DrawChart(g, chart.Value, margin, tableBottom + 40, Width - 2 * margin, Height - tableBottom - 120);

            return bmp;
        }

        private static bool IsNumeric(Cell cell, XlsxDoc doc) => ResolveNumber(cell, doc).HasValue;

        private static void DrawCellText(Graphics g, string text, Rectangle rect, bool bold, bool rightAlign, Color fontColor)
        {
            using var font = new Font("Segoe UI", 16, bold ? FontStyle.Bold : FontStyle.Regular);
            using var brush = new SolidBrush(fontColor);
            using var format = new StringFormat
            {
                Alignment = rightAlign ? StringAlignment.Far : StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap,
            };
            var bounds = Rectangle.Inflate(rect, -12, 0);
            g.DrawString(text, font, brush, bounds, format);
        }

        private static Color DarkOn(string fillHex)
        {
            if (fillHex.Length != 6) return Color.Black;
            var c = ColorTranslator.FromHtml("#" + fillHex);
            return (c.R * 299 + c.G * 587 + c.B * 114) / 1000 < 160 ? Color.White : Color.Black;
        }

        // Chart data: categories = first column of the primary table, series = numeric columns.
        // The Total row (if present) is excluded from BOTH categories and values, so it does not
        // scale the Y axis and dwarf the monthly bars.
        private static (List<string> Categories, List<(string Name, double[] Values)> Series)? BuildChart(
            Sheet sheet, XlsxDoc doc, int maxR, int cMin, int cMax)
        {
            var catCol = cMin;
            var dataRows = new List<int>();
            var categories = new List<string>();
            for (int r = 1; r <= maxR; r++)
            {
                var cell = sheet.Get(r, catCol);
                var v = cell != null ? Resolve(cell, doc) : null;
                if (string.IsNullOrWhiteSpace(v) || v!.Equals("Total", StringComparison.OrdinalIgnoreCase)) continue;
                dataRows.Add(r);
                categories.Add(v!);
            }
            if (categories.Count < 2) return null;

            var series = new List<(string, double[])>();
            for (int c = cMin + 1; c <= cMax; c++)
            {
                var name = Resolve(sheet.Get(0, c), doc) ?? $"Col {c + 1}";
                var values = dataRows
                    .Select(r => ResolveNumber(sheet.Get(r, c), doc) ?? double.NaN)
                    .ToArray();
                var real = values.Where(v => !double.IsNaN(v)).ToList();
                if (real.Count >= 2 && real.Any(v => v != 0))
                    series.Add((name, values));
            }
            if (series.Count == 0) return null;
            return (categories, series);
        }

        private static void DrawChart(Graphics g, (List<string> Categories, List<(string Name, double[] Values)> Series) chart,
            int x, int y, int w, int h)
        {
            var cats = chart.Categories;
            var series = chart.Series;
            using var titleFont = new Font("Segoe UI", 20, FontStyle.Bold);
            var title = string.Join(" vs ", series.Select(s => s.Name));
            g.DrawString(title, titleFont, Brushes.Black, x, y);

            int plotLeft = x + 90, plotRight = x + w - 30, plotTop = y + 50, plotBottom = y + h - 70;
            var maxVal = series.SelectMany(s => s.Values).Where(v => !double.IsNaN(v)).DefaultIfEmpty(1).Max();
            maxVal = Math.Max(1, maxVal * 1.1);

            using var axisFont = new Font("Segoe UI", 13);
            // Y grid + labels.
            for (int i = 0; i <= 4; i++)
            {
                var val = maxVal * i / 4;
                var yy = plotBottom - (plotBottom - plotTop) * i / 4;
                using var pen = new Pen(Color.FromArgb(220, 220, 220));
                g.DrawLine(pen, plotLeft, yy, plotRight, yy);
                var label = FormatNumber(val, "#,##0");
                var size = g.MeasureString(label, axisFont);
                g.DrawString(label, axisFont, Brushes.Gray, plotLeft - size.Width - 8, yy - size.Height / 2);
            }

            var groupWidth = (double)(plotRight - plotLeft) / cats.Count;
            var barWidth = groupWidth * 0.8 / series.Count;
            var palette = new[] { "#4472C4", "#ED7D31", "#70AD47", "#FFC000" };

            for (int ci = 0; ci < cats.Count; ci++)
            {
                var cx = plotLeft + groupWidth * ci + groupWidth * 0.1;
                for (int si = 0; si < series.Count; si++)
                {
                    var v = series[si].Values.Length > ci ? series[si].Values[ci] : double.NaN;
                    if (double.IsNaN(v)) continue;
                    var barH = (int)((plotBottom - plotTop) * v / maxVal);
                    using var brush = new SolidBrush(ColorTranslator.FromHtml(palette[si % palette.Length]));
                    g.FillRectangle(brush, (float)(cx + si * barWidth), plotBottom - barH, (float)barWidth, barH);
                }
                // Category label.
                var catSize = g.MeasureString(cats[ci], axisFont);
                g.DrawString(cats[ci], axisFont, Brushes.Gray,
                    (float)(cx + groupWidth / 2 - catSize.Width / 2), plotBottom + 8);
            }

            // Axis lines + legend.
            using (var axisPen = new Pen(Color.DimGray, 2))
            {
                g.DrawLine(axisPen, plotLeft, plotTop, plotLeft, plotBottom);
                g.DrawLine(axisPen, plotLeft, plotBottom, plotRight, plotBottom);
            }
            using var legendFont = new Font("Segoe UI", 15);
            var lx = plotLeft;
            var ly = plotBottom + 40;
            for (int si = 0; si < series.Count; si++)
            {
                using var brush = new SolidBrush(ColorTranslator.FromHtml(palette[si % palette.Length]));
                g.FillRectangle(brush, lx, ly + 4, 22, 22);
                var nameSize = g.MeasureString(series[si].Name, legendFont);
                g.DrawString(series[si].Name, legendFont, Brushes.Black, lx + 30, ly);
                lx += 30 + (int)nameSize.Width + 40;
            }
        }
    }
}
