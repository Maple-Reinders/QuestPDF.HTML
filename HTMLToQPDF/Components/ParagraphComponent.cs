using System;
using System.Collections.Generic;
using System.Linq;
using HtmlAgilityPack;
using HTMLQuestPDF.Extensions;
using HTMLQuestPDF.Utils;
using HTMLToQPDF.Components;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace HTMLQuestPDF.Components
{
    internal class ParagraphComponent : IComponent
    {
        private readonly List<HtmlNode> lineNodes;
        private readonly Dictionary<string, TextStyle> textStyles;
        private readonly Dictionary<string, Action<TextDescriptor>> paragraphStyles;

        public ParagraphComponent(List<HtmlNode> lineNodes, HTMLComponentsArgs args)
        {
            this.lineNodes = lineNodes;
            this.textStyles = args.TextStyles;
            this.paragraphStyles = args.ParagraphStyles;
        }

        private HtmlNode? GetParrentBlock(HtmlNode node)
        {
            if (node == null) return null;
            return node.IsBlockNode() ? node : GetParrentBlock(node.ParentNode);
        }

        private HtmlNode? GetListItemNode(HtmlNode node)
        {
            if (node == null || node.IsList()) return null;
            return node.IsListItem() ? node : GetListItemNode(node.ParentNode);
        }

        public void Compose(IContainer container)
        {
            var listItemNode = GetListItemNode(lineNodes.First()) ?? GetParrentBlock(lineNodes.First());
            if (listItemNode == null) return;

            var numberInList = listItemNode.GetNumberInList();

            if (numberInList != -1 || listItemNode.GetListNode() != null)
            {
                container.Row(row =>
                {
                    var listPrefix = numberInList == -1 ? "" : numberInList == 0 ? "•  " : $"{numberInList}. ";
                    row.AutoItem().MinWidth(26).AlignCenter().Text(listPrefix);
                    container = row.RelativeItem();
                });
            }

            var first = lineNodes.First();
            var last = lineNodes.First();

            first.InnerHtml = first.InnerHtml.TrimStart();
            last.InnerHtml = last.InnerHtml.TrimEnd();

            container.Text(text =>
            {
                ApplyParagraphStyles(text);
                lineNodes.ForEach(node => GetAction(node).Invoke(text));
            });
        }

        private void ApplyParagraphStyles(TextDescriptor text)
        {
            // Apply default "*" style first
            if (paragraphStyles.TryGetValue("*", out var defaultStyle))
            {
                defaultStyle(text);
            }

            // Apply tag-specific style (overrides default)
            var parentBlock = GetParrentBlock(lineNodes.First());
            if (parentBlock != null && paragraphStyles.TryGetValue(parentBlock.Name.ToLower(), out var tagStyle))
            {
                tagStyle(text);
            }
        }

        private Action<TextDescriptor> GetAction(HtmlNode node)
        {
            return text =>
            {
                if (node.NodeType == HtmlNodeType.Text)
                {
                    var span = text.Span(node.InnerText);
                    GetTextSpanAction(node).Invoke(span);
                }
                else if (node.IsBr())
                {
                    var span = text.Span("\n");
                    GetTextSpanAction(node).Invoke(span);
                }
                else
                {
                    foreach (var item in node.ChildNodes)
                    {
                        var action = GetAction(item);
                        action(text);
                    }
                }
            };
        }

        private TextSpanAction GetTextSpanAction(HtmlNode node)
        {
            return spanAction =>
            {
                var action = GetTextStyles(node);
                action(spanAction);
                if (node.ParentNode != null)
                {
                    var parrentAction = GetTextSpanAction(node.ParentNode);
                    parrentAction(spanAction);
                }
            };
        }

        public TextSpanAction GetTextStyles(HtmlNode element)
        {
            return (span) => span.Style(GetTextStyle(element));
        }

        public TextStyle GetTextStyle(HtmlNode element)
        {
            // Start with tag-based style or default
            var style = textStyles.TryGetValue(element.Name.ToLower(), out TextStyle? tagStyle)
                ? tagStyle
                : TextStyle.Default;

            // Apply inline CSS styles if present
            var styleAttr = element.GetAttributeValue("style", null);
            if (!string.IsNullOrEmpty(styleAttr))
            {
                var cssStyles = CssParser.ParseStyleAttribute(styleAttr);
                style = CssParser.ApplyTextStyles(style, cssStyles);
            }

            return style;
        }
    }
}