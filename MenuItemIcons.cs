using System.Collections.Generic;

namespace MicaPDF
{
    public static class MenuItemIcons
    {
        private static readonly Dictionary<string, string> Glyphs = new()
        {
            ["open"] = "\uE8E5",
            ["recentfiles"] = "\uE81C",
            ["print"] = "\uE749",
            ["savewithannotations"] = "\uE74E",
            ["zoomin"] = "\uE8A3",
            ["zoomout"] = "\uE71F",
            ["zoomreset"] = "\uE71E",
            ["zoomfit"] = "\uE9A6",
            ["find"] = "\uE721",
            ["outline"] = "\uE8FD",
            ["gotopage"] = "\uE8CB",
            ["nextpage"] = "\uE893",
            ["prevpage"] = "\uE892",
            ["doublepagemode"] = "\uE89A",
            ["coverpagemode"] = "\uE7AD",
            ["continuousmode"] = "\uF571",
            ["edit"] = "\uE70F",
            ["clearink"] = "\uE74D"
        };

        public static string GetGlyph(string tag) =>
            Glyphs.TryGetValue(tag, out var glyph) ? glyph : "\uE700";
    }
}
