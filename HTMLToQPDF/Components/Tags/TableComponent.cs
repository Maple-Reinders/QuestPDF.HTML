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
        /// True for a thead, tbody or tfoot element.
        /// </summary>
        private static bool IsSection(HtmlNode candidate)
        {
            var name = candidate.Name.ToLower();
            return name == "thead" || name == "tbody" || name == "tfoot";
        }

        /// <summary>
        /// Returns this table's own tr elements, in document order.
        /// </summary>
        private IEnumerable<HtmlNode> GetTableRows()
        {
            // A row sits either directly under the table or one level down inside a section. Anything
            // deeper belongs to a nested table and is rendered by that table's own component.
            foreach (var child in node.ChildNodes)
            {
                if (child.IsTr())
                    yield return child;
                else if (IsSection(child))
                {
                    foreach (var row in child.ChildNodes.Where(n => n.IsTr()))
                        yield return row;
                }
            }
        }

        /// <summary>
        /// Returns the rows of this table, each as the list of cells in one tr.
        /// </summary>
        private List<List<HtmlNode>> GetTableLines()
        {
            var lines = new List<List<HtmlNode>>();

            // Rows with no cells are kept, so that a rowspan reaching past them lands on the row the
            // markup intended
            foreach (var row in GetTableRows())
                lines.Add(row.ChildNodes.Where(IsCell).ToList());

            return lines;
        }

        protected override void ComposeMany(IContainer container)
        {
            var layout = GetTableLayout(GetTableLines());

            if (layout.Placements.Count == 0)
                return;

            var weights = GetColumnWeights(layout);
            var padding = GetCellPadding(layout.ColumnCount);

            // A table can declare more width than the page has. Scaling it down keeps the grid intact
            // instead of QuestPDF throwing a layout exception or wrapping cells to a character per line.
            var target = layout.ColumnCount > WideTableColumnCount ? container.ScaleToFit() : container;

            target.Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        // Relative weights always normalise to the space available, so a declared
                        // width can never push the table off the page
                        for (int i = 0; i < layout.ColumnCount; i++)
                            columns.RelativeColumn(weights[i]);
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
        /// Converts the declared column widths into QuestPDF relative weights.
        /// </summary>
        private static float[] GetColumnWeights(TableLayout layout)
        {
            var weights = new float[layout.ColumnCount];
            var declared = layout.ColumnWidths.Where(w => w.HasValue).Select(w => w.Value).ToList();

            // Nothing declared a width - equal share for every column
            if (declared.Count == 0)
            {
                for (int i = 0; i < weights.Length; i++)
                    weights[i] = 1f;

                return weights;
            }

            // Columns without a declared width get the average, so they stay visible
            var fallback = declared.Average();

            for (int i = 0; i < weights.Length; i++)
            {
                var declaredWidth = i < layout.ColumnWidths.Count ? layout.ColumnWidths[i] : null;

                // A zero weight would leave the column no space at all
                weights[i] = Math.Max(declaredWidth ?? fallback, 0.01f);
            }

            return weights;
        }

        /// <summary>
        /// Column count past which a table is treated as wide: padding is trimmed and the table is
        /// scaled to fit.
        /// </summary>
        private const int WideTableColumnCount = 12;

        /// <summary>
        /// Returns the cell padding to use for a table of the given column count.
        /// </summary>
        private static float GetCellPadding(int columnCount)
        {
            // Padding costs twice its value in width per cell, which adds up across many columns
            if (columnCount <= 10)
                return 5f;

            if (columnCount <= 20)
                return 2f;

            return 1f;
        }

        /// <summary>
        /// Resolves the grid: the position of every cell, the column count and the column widths.
        /// </summary>
        private TableLayout GetTableLayout(List<List<HtmlNode>> lines)
        {
            var layout = new TableLayout();
            var occupiedCells = new List<List<bool>>();

            for (int rowIndex = 0; rowIndex < lines.Count; rowIndex++)
            {
                foreach (var cell in lines[rowIndex])
                {
                    // Guard against colspan="0" / negative values in hand-written or pasted markup
                    var colSpan = (uint)Math.Max(1, cell.GetAttributeValue("colspan", 1));
                    var rowSpan = (uint)Math.Max(1, cell.GetAttributeValue("rowspan", 1));

                    var row = GetOccupiedCellsRow(occupiedCells, rowIndex);

                    // First column on this row not already taken by an earlier cell or a rowspan above
                    var col = 0;

                    while (col < row.Count && row[col])
                        col++;

                    // Mark the cell's full span as occupied, growing the grid to fit
                    for (int j = 0; j < rowSpan; j++)
                    {
                        var target = GetOccupiedCellsRow(occupiedCells, rowIndex + j);

                        for (int i = 0; i < colSpan; i++)
                        {
                            while (target.Count <= col + i)
                                target.Add(false);

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

            layout.ColumnCount = occupiedCells.Count == 0 ? 1 : Math.Max(1, occupiedCells.Max(r => r.Count));
            return layout;
        }

        /// <summary>
        /// Records the width a cell declares against the columns it covers.
        /// </summary>
        private static void RecordColumnWidth(List<float?> widths, HtmlNode cell, int col, int colSpan)
        {
            var declared = GetDeclaredWidth(cell);

            if (!declared.HasValue)
                return;

            // A spanning cell says nothing about how its width splits, so share it evenly
            var perColumn = declared.Value / colSpan;

            for (int i = 0; i < colSpan; i++)
            {
                while (widths.Count <= col + i)
                    widths.Add(null);

                // The first declaration for a column wins - it is the most specific
                if (!widths[col + i].HasValue)
                    widths[col + i] = perColumn;
            }
        }

        /// <summary>
        /// Returns the width a cell declares, or null if it declares none.
        /// </summary>
        private static float? GetDeclaredWidth(HtmlNode cell)
        {
            // The width attribute takes precedence over inline style
            var attribute = ParseLength(cell.GetAttributeValue("width", null));

            if (attribute.HasValue)
                return attribute;

            var style = cell.GetAttributeValue("style", null);

            if (string.IsNullOrWhiteSpace(style))
                return null;

            var match = Regex.Match(style, @"(?:^|;)\s*width\s*:\s*([^;]+)", RegexOptions.IgnoreCase);

            return match.Success ? ParseLength(match.Groups[1].Value) : null;
        }

        /// <summary>
        /// Returns the leading number in a CSS length, or null if there isn't one.
        /// </summary>
        private static float? ParseLength(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            // Units are ignored - these values only ever become relative weights
            var match = Regex.Match(raw, @"[0-9]*\.?[0-9]+");

            if (!match.Success)
                return null;

            if (!float.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return null;

            return value > 0 ? value : (float?)null;
        }

        /// <summary>
        /// Returns the occupied-cell row at the given index, adding empty rows until it exists.
        /// </summary>
        private static List<bool> GetOccupiedCellsRow(List<List<bool>> occupiedCells, int rowIndex)
        {
            while (occupiedCells.Count <= rowIndex)
                occupiedCells.Add(new List<bool>());

            return occupiedCells[rowIndex];
        }

    }
}