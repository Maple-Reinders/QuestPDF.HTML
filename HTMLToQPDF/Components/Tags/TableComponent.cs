using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using HTMLQuestPDF.Extensions;
using HTMLToQPDF.Components;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace HTMLQuestPDF.Components.Tags
{
    internal class TableComponent : BaseHTMLComponent
    {
        /// <summary>
        /// Where a single cell ends up in the grid. Columns/rows are 1-based, as QuestPDF expects.
        /// </summary>
        private class CellPlacement
        {
            public HtmlNode Cell = null!;
            public uint Column;
            public uint Row;
            public uint ColSpan;
            public uint RowSpan;
        }

        /// <summary>
        /// The resolved grid: where each cell goes, how many columns are needed, and the relative
        /// width of each column (null where the markup didn't declare one).
        /// </summary>
        private class TableLayout
        {
            public List<CellPlacement> Placements = new List<CellPlacement>();
            public int ColumnCount = 1;
            public List<float?> ColumnWidths = new List<float?>();
        }

        public TableComponent(HtmlNode node, HTMLComponentsArgs args) : base(node, args)
        {
        }

        /// <summary>
        /// True for a td or th element.
        /// </summary>
        private static bool IsCell(HtmlNode candidate)
        {
            var name = candidate.Name.ToLower();
            return name == "td" || name == "th";
        }

        /// <summary>
        /// The nearest enclosing table of a node, or null if it isn't inside one.
        /// </summary>
        private static HtmlNode? GetNearestTable(HtmlNode start)
        {
            var parent = start.ParentNode;

            while (parent != null)
            {
                if (parent.IsTable()) return parent;
                parent = parent.ParentNode;
            }

            return null;
        }

        /// <summary>
        /// The rows of *this* table - each entry is the cells of one tr belonging to us.
        ///
        /// Rows are read straight from the tr elements rather than by walking a flat list of cells
        /// and grouping consecutive ones by their parent tr. That older approach depended on the cell
        /// list arriving in document order; when it didn't, every cell was treated as its own row,
        /// which collapsed the whole table into a single narrow column.
        ///
        /// Rows whose nearest enclosing table isn't us belong to a nested table and are skipped -
        /// they get rendered by that table's own component. Excel and Word wrap cell content in
        /// nested tables, so without this a table swallows its children's rows as well as its own.
        /// </summary>
        private List<List<HtmlNode>> GetTableLines()
        {
            var lines = new List<List<HtmlNode>>();

            foreach (var row in node.Descendants("tr"))
            {
                if (GetNearestTable(row) != node) continue;

                // td/th are direct children of tr - anything deeper belongs to a nested table
                var cells = row.ChildNodes.Where(IsCell).ToList();
                if (cells.Count > 0) lines.Add(cells);
            }

            return lines;
        }

        protected override void ComposeMany(IContainer container)
        {
            var lines = GetTableLines();
            if (lines.Count == 0) return;

            var layout = GetTableLayout(lines);
            var weights = GetColumnWeights(layout);
            var padding = GetCellPadding(layout.ColumnCount);

            // A pasted spreadsheet can declare far more width than a portrait page has. Left alone
            // QuestPDF either throws ("conflicting size constraints") or wraps every cell down to a
            // character per line. Scaling the whole table down keeps it looking like the source grid
            // and keeps the page count sane, at the cost of smaller text on very wide tables only.
            var target = layout.ColumnCount > WideTableColumnCount ? container.ScaleToFit() : container;

            target.Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        for (int i = 0; i < layout.ColumnCount; i++)
                        {
                            // Relative (not constant) so the weights always normalise to the space
                            // available - a declared width can never push the table off the page.
                            columns.RelativeColumn(weights[i]);
                        }
                    });

                    foreach (var placement in layout.Placements)
                    {
                        table.Cell()
                        .ColumnSpan(placement.ColSpan)
                        .Column(placement.Column)
                        .Row(placement.Row)
                        .RowSpan(placement.RowSpan)
                        .Border(1)
                        .Padding(padding)
                        .Component(placement.Cell.GetComponent(args));
                    }
                });
        }

        /// <summary>
        /// Turns the declared column widths into QuestPDF relative weights.
        ///
        /// Columns the markup said nothing about get the average of the ones it did, so they stay
        /// visible rather than collapsing. If nothing declared a width at all, every column gets a
        /// weight of 1 - i.e. the original equal-share behaviour, so simple tables are unaffected.
        /// </summary>
        private static float[] GetColumnWeights(TableLayout layout)
        {
            var weights = new float[layout.ColumnCount];

            var declared = layout.ColumnWidths.Where(w => w.HasValue).Select(w => w!.Value).ToList();
            if (declared.Count == 0)
            {
                for (int i = 0; i < weights.Length; i++) weights[i] = 1f;
                return weights;
            }

            var fallback = declared.Average();

            for (int i = 0; i < weights.Length; i++)
            {
                var declaredWidth = i < layout.ColumnWidths.Count ? layout.ColumnWidths[i] : null;

                // Never let a column reach zero weight - QuestPDF would give it no space at all
                weights[i] = Math.Max(declaredWidth ?? fallback, 0.01f);
            }

            return weights;
        }

        /// <summary>
        /// Past this many columns a table is treated as "wide": padding is trimmed hard and the whole
        /// table is scaled to fit rather than being allowed to wrap itself to death.
        /// </summary>
        private const int WideTableColumnCount = 12;

        /// <summary>
        /// A flat 5pt of padding costs 10pt of width per cell, which is fine on a 3 column table and
        /// ruinous on a 30 column one - on Letter portrait it can eat more than half the page and
        /// squeeze text down to one character per line. Scale it back as the table gets wider.
        /// </summary>
        private static float GetCellPadding(int columnCount)
        {
            if (columnCount <= 10) return 5f;
            if (columnCount <= 20) return 2f;
            return 1f;
        }

        /// <summary>
        /// Works out where every cell sits before anything is rendered, and reports how many columns
        /// the grid actually needs.
        ///
        /// The occupancy grid grows on demand. The previous implementation sized it from colspans alone
        /// (max over rows of the summed colspan), which ignored rowspan carry-over from earlier rows: a
        /// row could fill up completely, and the "find the next free column" scan then walked straight
        /// off the end of the fixed-length array with an IndexOutOfRangeException.
        /// </summary>
        private TableLayout GetTableLayout(List<List<HtmlNode>> lines)
        {
            var layout = new TableLayout();
            var occupancy = new List<List<bool>>();

            for (int rowIndex = 0; rowIndex < lines.Count; rowIndex++)
            {
                foreach (var cell in lines[rowIndex])
                {
                    // Guard against colspan="0" / negative values in hand-written or pasted markup
                    var colSpan = (uint)Math.Max(1, cell.GetAttributeValue("colspan", 1));
                    var rowSpan = (uint)Math.Max(1, cell.GetAttributeValue("rowspan", 1));

                    var row = GetOccupancyRow(occupancy, rowIndex);

                    // First column on this row not already taken by an earlier cell or a rowspan above
                    var col = 0;
                    while (col < row.Count && row[col]) col++;

                    // Mark the cell's full span as occupied, growing the grid to fit
                    for (int j = 0; j < rowSpan; j++)
                    {
                        var target = GetOccupancyRow(occupancy, rowIndex + j);
                        for (int i = 0; i < colSpan; i++)
                        {
                            while (target.Count <= col + i) target.Add(false);
                            target[col + i] = true;
                        }
                    }

                    RecordColumnWidth(layout.ColumnWidths, cell, col, (int)colSpan);

                    layout.Placements.Add(new CellPlacement
                    {
                        Cell = cell,
                        Column = (uint)col + 1,
                        Row = (uint)rowIndex + 1,
                        ColSpan = colSpan,
                        RowSpan = rowSpan
                    });
                }
            }

            layout.ColumnCount = occupancy.Count == 0 ? 1 : Math.Max(1, occupancy.Max(r => r.Count));
            return layout;
        }

        /// <summary>
        /// Notes the width a cell declares against the columns it covers.
        ///
        /// The first declaration for a column wins. Excel and Word paste their column widths onto a
        /// leading spacer row of empty cells, so the narrowest and most specific values come first;
        /// a later wide cell spanning several columns must not overwrite them.
        /// </summary>
        private static void RecordColumnWidth(List<float?> widths, HtmlNode cell, int col, int colSpan)
        {
            var declared = GetDeclaredWidth(cell);
            if (!declared.HasValue) return;

            // A cell spanning N columns says nothing about how its width splits between them, so
            // share it evenly across whichever of those columns are still unknown.
            var perColumn = declared.Value / colSpan;

            for (int i = 0; i < colSpan; i++)
            {
                while (widths.Count <= col + i) widths.Add(null);
                if (!widths[col + i].HasValue) widths[col + i] = perColumn;
            }
        }

        /// <summary>
        /// Reads a cell's width, preferring the legacy width attribute - that is what Excel and Word
        /// emit, and CssParser only ever looks at inline style, so without this the values are lost.
        /// </summary>
        private static float? GetDeclaredWidth(HtmlNode cell)
        {
            var attribute = ParseLength(cell.GetAttributeValue("width", null));
            if (attribute.HasValue) return attribute;

            var style = cell.GetAttributeValue("style", null);
            if (string.IsNullOrWhiteSpace(style)) return null;

            var match = Regex.Match(style, @"(?:^|;)\s*width\s*:\s*([^;]+)", RegexOptions.IgnoreCase);
            return match.Success ? ParseLength(match.Groups[1].Value) : null;
        }

        /// <summary>
        /// Pulls the leading number out of a length. Units are deliberately ignored: the values only
        /// ever become relative weights, so px/pt/% all work as long as a table is self-consistent.
        /// </summary>
        private static float? ParseLength(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var match = Regex.Match(raw, @"[0-9]*\.?[0-9]+");
            if (!match.Success) return null;

            if (!float.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return null;

            return value > 0 ? value : (float?)null;
        }

        /// <summary>
        /// Returns the occupancy row at the given index, adding empty rows until it exists.
        /// </summary>
        private static List<bool> GetOccupancyRow(List<List<bool>> occupancy, int rowIndex)
        {
            while (occupancy.Count <= rowIndex) occupancy.Add(new List<bool>());
            return occupancy[rowIndex];
        }

    }
}