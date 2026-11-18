using System.Globalization;
using System.Net;
using System.Text;

namespace Game.Api.Components;

/// <summary>
/// The still parts of the zone map, drawn as pixel art in SVG: the night sky,
/// the fields with a farmhouse and a windmill, the wall, the town and the
/// people of the plains. One unit of the 300 by 104 picture is one pixel.
/// Everything here is drawn once and never changes while the page is open.
/// </summary>
internal static class PixelArt
{
    public const int Width = 300;
    public const int Height = 104;
    public const int Ground = 96;

    /// <summary>Where each of the four people of the plains stands, by kind.</summary>
    public static readonly (string Kind, int X)[] People =
    [
        ("citizens", 18),
        ("garrison", 42),
        ("raiders", 66),
        ("travellers", 90),
    ];

    /// <summary>The four doors of the notice board, left to right, by the port's name.</summary>
    public static readonly (string Name, int X)[] Doors =
    [
        ("http", 170),
        ("management", 194),
        ("debug", 218),
        ("database", 242),
    ];

    public const int DoorWidth = 12;
    public const int DoorTop = 74;
    public const int GateX = 130;
    public const int GateWidth = 12;

    private static readonly Dictionary<char, string> Palette = new()
    {
        ['k'] = "#15130f", // outline
        ['s'] = "#e0b48a", // skin
        ['h'] = "#d9b44a", // straw
        ['g'] = "#4f7a3a", // green cloth
        ['b'] = "#6b4a2b", // brown cloth
        ['y'] = "#3a5ea8", // blue tabard
        ['m'] = "#9aa3ab", // metal
        ['r'] = "#b33a2e", // red mask
        ['d'] = "#3b3036", // dark cloth
        ['c'] = "#b08a3e", // ochre cloak
        ['w'] = "#7a5a32", // wood
    };

    // Eight pixels wide, thirteen tall, feet on the last row.
    private static readonly Dictionary<string, string[]> Figures = new()
    {
        ["citizens"] =
        [
            "..hhhh..",
            ".hhhhhh.",
            "..ssss..",
            "..sksk..",
            "..ssss..",
            ".gggggg.",
            "sggggggs",
            ".gggggg.",
            ".gggggg.",
            ".bbbbbb.",
            ".bb..bb.",
            ".bb..bb.",
            ".kk..kk.",
        ],
        ["garrison"] =
        [
            "..mmmm.m",
            ".mmmmmmw",
            "..ssss.w",
            "..sksk.w",
            "..ssss.w",
            ".yyyyysw",
            "syyyyy.w",
            ".yymyy.w",
            ".yyyyy.w",
            ".bbbbb.w",
            ".bb.bb.w",
            ".bb.bb.w",
            ".kk.kk.w",
        ],
        ["raiders"] =
        [
            "..dddd..",
            ".dddddd.",
            ".dssssd.",
            ".dskskd.",
            ".drrrrd.",
            ".dddddd.",
            "sdddddds",
            ".dddddd.",
            ".ddmddd.",
            ".dddddd.",
            ".dd..dd.",
            ".dd..dd.",
            ".kk..kk.",
        ],
        ["travellers"] =
        [
            "...cc..w",
            "..cccc.w",
            ".ccsscc.",
            ".cskskcw",
            ".ccsscc.",
            ".cccccsw",
            "cccccc.w",
            "cccccc.w",
            "cccccc.w",
            "cccccc.w",
            ".cc.cc.w",
            ".cc.cc.w",
            ".kk.kk.w",
        ],
    };

    /// <summary>A small walking figure in a kind's colour, for traffic during a wave.</summary>
    public static readonly Dictionary<string, string> TrafficColours = new()
    {
        ["citizens"] = "#7fbf6a",
        ["garrison"] = "#6aa8d8",
        ["raiders"] = "#e06a50",
        ["travellers"] = "#e0b050",
    };

    public static string Background(Func<string, string> label)
    {
        var svg = new StringBuilder();

        // Night sky, banded from deep to lighter towards the horizon.
        string[] sky = ["#141a2b", "#172036", "#1b2540", "#202b48", "#25314f"];
        for (var i = 0; i < sky.Length; i++) Rect(svg, 0, i * 13, Width, 13, sky[i]);
        var random = new Random(7);
        for (var i = 0; i < 46; i++)
        {
            var x = random.Next(Width);
            var y = random.Next(44);
            // Keep the sky clear behind the names of the two sides.
            if (y < 12 && (x < 64 || x is > 160 and < 232)) continue;
            Rect(svg, x, y, 1, 1, i % 5 == 0 ? "#f1e9c8" : "#9aa6c8", i % 7 == 0 ? "twinkle" : null);
        }
        Rect(svg, 238, 8, 6, 6, "#e8e2c4");
        Rect(svg, 237, 9, 1, 4, "#e8e2c4");
        Rect(svg, 244, 9, 1, 4, "#e8e2c4");
        Rect(svg, 240, 9, 2, 2, "#d3ccad");

        // Hills on the horizon.
        Steps(svg, [(0, 56), (20, 52), (40, 50), (64, 53), (90, 49), (124, 54)], 64, "#1d2a22");
        Steps(svg, [(148, 54), (170, 50), (200, 47), (230, 51), (260, 48), (300, 52)], 64, "#1b2622");

        // The fields: grass, then furrows.
        Rect(svg, 0, 64, 124, 40, "#33502a");
        for (var row = 0; row < 6; row++) Rect(svg, 4, 66 + row * 2, 114, 1, row % 2 == 0 ? "#5a4a2a" : "#4a3c22");
        for (var x = 2; x < 120; x += 9) Rect(svg, x, 80, 1, 2, "#476b34");
        Rect(svg, 0, 84, 124, 12, "#3b5a2e");
        Rect(svg, 0, 96, 124, 8, "#2b4223");

        // A farmhouse with a lit window.
        Rect(svg, 8, 50, 28, 14, "#c8b48a");
        Rect(svg, 8, 50, 28, 1, "#a8946a");
        for (var i = 0; i < 6; i++) Rect(svg, 6 + i * 2, 49 - i * 2, 32 - i * 4, 2, i % 2 == 0 ? "#8b2f2a" : "#7a2824");
        Rect(svg, 26, 56, 5, 8, "#5a3a22");
        Rect(svg, 12, 54, 6, 5, "#f0c860", "window");
        Rect(svg, 14, 54, 1, 5, "#8a6a32");
        Rect(svg, 12, 56, 6, 1, "#8a6a32");

        // A windmill whose sails turn.
        for (var i = 0; i < 28; i++) Rect(svg, 47 - i / 7, 36 + i, 6 + 2 * (i / 7), 1, i % 4 == 0 ? "#9a8a72" : "#b5a488");
        Rect(svg, 48, 56, 4, 8, "#5a3a22");
        svg.Append("<g class=\"sails\"><animateTransform attributeName=\"transform\" type=\"rotate\" from=\"0 50 34\" to=\"360 50 34\" dur=\"14s\" repeatCount=\"indefinite\" />");
        Rect(svg, 49, 18, 2, 32, "#6b5a44");
        Rect(svg, 34, 33, 32, 2, "#6b5a44");
        Rect(svg, 51, 19, 4, 13, "#d8ccb0");
        Rect(svg, 45, 36, 4, 13, "#d8ccb0");
        Rect(svg, 35, 29, 13, 4, "#d8ccb0");
        Rect(svg, 52, 35, 13, 4, "#d8ccb0");
        svg.Append("</g>");
        Rect(svg, 49, 33, 2, 2, "#3a2e22");

        // A fence along the road.
        for (var x = 2; x < 122; x += 6) Rect(svg, x, 82, 1, 4, "#7a5a32");
        Rect(svg, 0, 83, 124, 1, "#8a6a3e");

        // The town: houses, cobbles and a tower with a flag.
        Rect(svg, 148, 64, 152, 32, "#3a3a38");
        for (var y = 66; y < 96; y += 3)
            for (var x = 148 + (y % 2) * 3; x < Width; x += 6) Rect(svg, x, y, 4, 1, "#444442");
        Rect(svg, 148, 96, 152, 8, "#2c2c2a");
        House(svg, 264, 44, 12, "#6a5a48", "#3a4a6a");
        Rect(svg, 280, 14, 14, 50, "#7a7c80");
        for (var y = 16; y < 64; y += 4) Rect(svg, 280, y, 14, 1, "#6a6c70");
        for (var x = 280; x < 294; x += 4) Rect(svg, x, 10, 2, 4, "#7a7c80");
        Rect(svg, 285, 24, 4, 6, "#f0c860", "window");
        Rect(svg, 286, 2, 1, 9, "#5a4a3a");
        svg.Append("<g class=\"flag\">");
        Rect(svg, 287, 2, 7, 4, "#3a5ea8");
        Rect(svg, 289, 3, 3, 2, "#d9b44a");
        svg.Append("</g>");

        // The notice board's hall: walls, roof and its sign.
        Rect(svg, 160, 56, 102, 40, "#6b4a2b");
        for (var y = 58; y < 96; y += 4) Rect(svg, 160, y, 102, 1, "#5a3e24");
        for (var i = 0; i < 4; i++) Rect(svg, 156 + i * 2, 54 - i * 2, 110 - i * 4, 2, i % 2 == 0 ? "#4a3a5a" : "#3e304e");
        Rect(svg, 186, 36, 50, 10, "#8a6a3e");
        Rect(svg, 186, 36, 50, 1, "#a8844e");
        Rect(svg, 186, 45, 50, 1, "#5a3e24");
        Rect(svg, 196, 46, 1, 2, "#5a3e24");
        Rect(svg, 225, 46, 1, 2, "#5a3e24");
        Text(svg, 211, 42.6, label("board"), "sign");

        // The wall between the fields and the town, with its gate arch.
        Rect(svg, 124, 24, 24, 80, "#62666a");
        for (var y = 26; y < 104; y += 4)
            for (var x = 124 + (y / 4 % 2) * 3; x < 148; x += 6) Rect(svg, x, y, 5, 1, "#55595d");
        for (var x = 124; x < 148; x += 6) Rect(svg, x, 20, 4, 4, "#62666a");
        Rect(svg, GateX - 1, 70, GateWidth + 2, 2, "#4a4d50");
        Rect(svg, GateX, 72, GateWidth, Ground - 72, "#0e0f0d");

        // The people of the plains, and their names from the theme.
        foreach (var (kind, x) in People)
        {
            svg.Append($"<g class=\"figure {kind}\">");
            Sprite(svg, Figures[kind], x - 4, Ground - 13);
            svg.Append("</g>");
            Text(svg, x, 102, label(kind), "person");
        }

        // The names of the two sides.
        Text(svg, 6, 8, label("plains").ToUpperInvariant(), "zone", anchor: "start");
        Text(svg, 228, 8, label("town").ToUpperInvariant(), "zone", anchor: "end");

        return svg.ToString();
    }

    private static void House(StringBuilder svg, int x, int y, int width, string wall, string roof)
    {
        Rect(svg, x, y + 6, width, 64 - y - 6, wall);
        for (var i = 0; i < 3; i++) Rect(svg, x - 1 + i * 2, y + 4 - i * 2, width + 2 - i * 4, 2, roof);
        Rect(svg, x + width / 2 - 2, y + 10, 3, 3, "#f0c860", "window");
    }

    // A skyline from left to right, each point the height of a flat step.
    private static void Steps(StringBuilder svg, (int X, int Y)[] points, int bottom, string colour)
    {
        for (var i = 0; i < points.Length - 1; i++)
        {
            var (x, y) = points[i];
            var next = points[i + 1].X;
            Rect(svg, x, y, next - x, bottom - y, colour);
        }
    }

    // Rows of palette letters; runs of one colour become one rectangle.
    private static void Sprite(StringBuilder svg, string[] rows, int x, int y)
    {
        for (var row = 0; row < rows.Length; row++)
        {
            var line = rows[row];
            var start = 0;
            while (start < line.Length)
            {
                var c = line[start];
                var end = start;
                while (end < line.Length && line[end] == c) end++;
                if (c != '.') Rect(svg, x + start, y + row, end - start, 1, Palette[c]);
                start = end;
            }
        }
    }

    public static void Rect(StringBuilder svg, double x, double y, double width, double height, string fill, string? cssClass = null) =>
        svg.Append(string.Create(CultureInfo.InvariantCulture,
            $"<rect x=\"{x}\" y=\"{y}\" width=\"{width}\" height=\"{height}\" fill=\"{fill}\"{(cssClass is null ? "" : $" class=\"{cssClass}\"")} />"));

    private static void Text(StringBuilder svg, double x, double y, string text, string cssClass, string anchor = "middle") =>
        svg.Append(string.Create(CultureInfo.InvariantCulture,
            $"<text x=\"{x}\" y=\"{y}\" text-anchor=\"{anchor}\" class=\"{cssClass}\">{WebUtility.HtmlEncode(text)}</text>"));
}
