using HtmlAgilityPack;
using HTMLQuestPDF.Extensions;
using HTMLToQPDF.Components;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace HTMLQuestPDF.Components.Tags
{
    internal class ListComponent : BaseHTMLComponent
    {
        public ListComponent(HtmlNode node, HTMLComponentsArgs args) : base(node, args)
        {
        }

        protected override IContainer ApplyStyles(IContainer container)
        {
            var first = IsFirstList(node);
            return base.ApplyStyles(container).Element(e =>
            {
                var result = e.PaddingVertical(args.ListVerticalPadding);
                if (first)
                    result.PaddingHorizontal(0);
                else
                    result.PaddingLeft(25);

                return result;
            });
        }

        private bool IsFirstList(HtmlNode node)
        {
            if (node.ParentNode == null) return true;
            return !node.ParentNode.IsList() && IsFirstList(node.ParentNode);
        }
    }
}