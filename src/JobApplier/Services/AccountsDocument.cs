using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using JobApplier.Domain;

namespace JobApplier.Services;

/// <summary>
/// Renders one Word document with two tables: the accounts the agent created (company, role, site, e-mail, password) and the
/// postings it applied to (company, role, URL, result, date). The whole file is rebuilt on every change, so it is always
/// consistent with <c>accounts.json</c> and <c>applications.json</c>.
/// </summary>
public static class AccountsDocument
{
    // A4 landscape, 0.5" margins -> 15398 twips of usable width. Both tables add up to it.
    private static readonly (string Header, int Width)[] Columns =
    {
        ("Company", 2300), ("Role", 2700), ("Site", 3100), ("E-mail", 3200), ("Password", 2300), ("Created", 1798)
    };

    private static readonly (string Header, int Width)[] ApplicationColumns =
    {
        ("Company", 2300), ("Role", 3000), ("Posting URL", 6700), ("Result", 1700), ("Applied", 1698)
    };

    public static void Write(string path, IReadOnlyList<AccountRecord> records, IReadOnlyList<ApplicationRecord>? applications = null)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, "~JobApplier-" + Guid.NewGuid().ToString("N")[..8] + ".tmp");

        try
        {
            using (var document = WordprocessingDocument.Create(temp, WordprocessingDocumentType.Document))
            {
                var main = document.AddMainDocumentPart();
                main.Document = new Document(Build(records, applications ?? Array.Empty<ApplicationRecord>()));
                main.Document.Save();
            }
            // If Word has the target open this throws IOException; the caller reports it and the data stays in accounts.json.
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch (IOException) { /* leave it */ }
            }
        }
    }

    private static Body Build(IReadOnlyList<AccountRecord> records, IReadOnlyList<ApplicationRecord> applications)
    {
        var body = new Body();

        body.Append(Paragraph("Job application accounts", bold: true, size: 40, after: 60));
        body.Append(Paragraph(
            "Accounts created by JobApplier while applying. This file contains passwords in plain text - keep it private.",
            size: 20, color: "666666", after: 200));

        body.Append(Table(
            Columns,
            records.OrderBy(r => r.CreatedUtc).Select(r => new[]
            {
                r.Company, r.Role, r.Site, r.Email, r.Password, r.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            }),
            "No accounts yet.",
            monospaceColumn: 4));

        body.Append(Paragraph("Applications", bold: true, size: 32, before: 480, after: 60));
        body.Append(Paragraph(
            "Postings JobApplier has applied to. A new job whose posting address matches one of these is skipped, so the same position is never applied to twice.",
            size: 20, color: "666666", after: 200));

        body.Append(Table(
            ApplicationColumns,
            applications.OrderBy(a => a.AppliedUtc).Select(a => new[]
            {
                a.Company, a.Role, a.Url, a.Result == ApplyResultKind.AlreadyApplied ? "Already applied" : "Submitted",
                a.AppliedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            }),
            "No applications yet."));

        body.Append(new SectionProperties(
            new PageSize { Width = 16838U, Height = 11906U, Orient = PageOrientationValues.Landscape },
            new PageMargin { Top = 720, Right = 720U, Bottom = 720, Left = 720U, Header = 360U, Footer = 360U, Gutter = 0U }));
        return body;
    }

    /// <summary>A bordered fixed-width table: shaded header row, one row per entry, or a single spanning row when there are none.</summary>
    private static Table Table((string Header, int Width)[] columns, IEnumerable<string[]> rows, string emptyText, int monospaceColumn = -1)
    {
        var table = new Table(
            new TableProperties(
                new TableWidth { Width = "15398", Type = TableWidthUnitValues.Dxa },
                new TableLayout { Type = TableLayoutValues.Fixed },
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 4, Color = "BBBBBB" },
                    new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "BBBBBB" },
                    new LeftBorder { Val = BorderValues.Single, Size = 4, Color = "BBBBBB" },
                    new RightBorder { Val = BorderValues.Single, Size = 4, Color = "BBBBBB" },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "DDDDDD" },
                    new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = "DDDDDD" }),
                new TableCellMarginDefault(
                    new TopMargin { Width = "60", Type = TableWidthUnitValues.Dxa },
                    new BottomMargin { Width = "60", Type = TableWidthUnitValues.Dxa },
                    new TableCellLeftMargin { Width = 100, Type = TableWidthValues.Dxa },
                    new TableCellRightMargin { Width = 100, Type = TableWidthValues.Dxa })));

        var grid = new TableGrid();
        foreach (var (_, width) in columns) grid.Append(new GridColumn { Width = width.ToString() });
        table.Append(grid);

        var header = new TableRow(new TableRowProperties(new TableHeader()));
        foreach (var (title, width) in columns) header.Append(Cell(title, width, bold: true, shade: "E8EEF7"));
        table.Append(header);

        var any = false;
        foreach (var values in rows)
        {
            any = true;
            var row = new TableRow(new TableRowProperties(new CantSplit()));
            for (var i = 0; i < columns.Length; i++) row.Append(Cell(values[i], columns[i].Width, mono: i == monospaceColumn));
            table.Append(row);
        }

        if (!any)
        {
            var empty = new TableRow();
            var cell = Cell(emptyText, columns.Sum(c => c.Width));
            cell.TableCellProperties!.Append(new GridSpan { Val = columns.Length });
            empty.Append(cell);
            table.Append(empty);
        }

        return table;
    }

    private static Paragraph Paragraph(string text, bool bold = false, int size = 22, string? color = null, int before = 0, int after = 0)
    {
        var props = new RunProperties(new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" }, new FontSize { Val = size.ToString() });
        if (bold) props.Append(new Bold());
        if (color != null) props.Append(new Color { Val = color });

        return new Paragraph(
            new ParagraphProperties(new SpacingBetweenLines { Before = before.ToString(), After = after.ToString() }),
            new Run(props, new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
    }

    private static TableCell Cell(string text, int width, bool bold = false, bool mono = false, string? shade = null)
    {
        var props = new RunProperties(
            new RunFonts { Ascii = mono ? "Consolas" : "Calibri", HighAnsi = mono ? "Consolas" : "Calibri" },
            new FontSize { Val = "20" });
        if (bold) props.Append(new Bold());

        var cellProps = new TableCellProperties(new TableCellWidth { Width = width.ToString(), Type = TableWidthUnitValues.Dxa });
        if (shade != null) cellProps.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = shade, Color = "auto" });

        return new TableCell(
            cellProps,
            new Paragraph(
                new ParagraphProperties(new SpacingBetweenLines { After = "0" }),
                new Run(props, new Text(text ?? string.Empty) { Space = SpaceProcessingModeValues.Preserve })));
    }
}
