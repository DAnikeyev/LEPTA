using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using LEPTA.Theming;

namespace LEPTA.Services;

internal static class MermaidDiagramPalettePostProcessor
{
    internal const string AppliedMarker = "%% LEPTA_MERMAID_PALETTE %%";

    private static readonly Regex FlowchartHeaderRegex = new(
        @"^\s*(graph|flowchart)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly Regex StyleLineRegex = new(
        @"^\s*style\s+(?<ids>[^\s]+)\s+(?<props>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ClassDefLineRegex = new(
        @"^\s*classDef\s+(?<names>[^\s]+)\s+(?<props>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ClassAssignmentLineRegex = new(
        @"^\s*class\s+(?<ids>[^\s]+)\s+(?<classes>[^;]+?)\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex NodeTokenRegex = new(
        @"(?<![\w-])(?<id>[A-Za-z_][\w-]*)\s*(?<shape>\(\(|\(\[|\[\[|\[\(|\[/|\[\\|\{\{|\{|\[|\()",
        RegexOptions.CultureInvariant);

    private static readonly Regex RgbColorRegex = new(
        @"rgb\(\s*(?<r>\d{1,3})\s*,\s*(?<g>\d{1,3})\s*,\s*(?<b>\d{1,3})\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly IReadOnlyDictionary<string, Color> NamedColors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
    {
        ["white"] = Color.FromRgb(0xFF, 0xFF, 0xFF),
        ["black"] = Color.FromRgb(0x00, 0x00, 0x00),
        ["red"] = Color.FromRgb(0xE1, 0x4B, 0x5A),
        ["green"] = Color.FromRgb(0x3B, 0xB2, 0x73),
        ["blue"] = Color.FromRgb(0x2F, 0x6F, 0xED),
        ["yellow"] = Color.FromRgb(0xF5, 0xC7, 0x6B),
        ["orange"] = Color.FromRgb(0xE9, 0x8B, 0x2A),
        ["purple"] = Color.FromRgb(0x8B, 0x5C, 0xE1),
        ["pink"] = Color.FromRgb(0xD9, 0x46, 0xEF),
        ["gray"] = Color.FromRgb(0x94, 0xA3, 0xB8),
        ["grey"] = Color.FromRgb(0x94, 0xA3, 0xB8),
        ["brown"] = Color.FromRgb(0x92, 0x66, 0x30),
        ["cyan"] = Color.FromRgb(0x22, 0xC5, 0xD6),
        ["teal"] = Color.FromRgb(0x14, 0xB8, 0xA6)
    };

    private static readonly MermaidPalette DarkPalette = new(
        IsDark: true,
        TextPrimary: "#F4F6FA",
        TextMuted: "#D6DEE9",
        LineColor: "#90A6C4",
        ClusterBackground: "#1B2635",
        ClusterBorder: "#4A5D78",
        Primary: new MermaidPaletteEntry("#2B4268", "#7EA6FF", "#F4F6FA"),
        Success: new MermaidPaletteEntry("#1F5E4A", "#5ACB96", "#F4F6FA"),
        Warning: new MermaidPaletteEntry("#6B4E16", "#E7B84E", "#F4F6FA"),
        Danger: new MermaidPaletteEntry("#6A2D42", "#E58AAA", "#F4F6FA"),
        Neutral: new MermaidPaletteEntry("#38414F", "#A8B3C2", "#D6DEE9"));

    private static readonly MermaidPalette LightPalette = new(
        IsDark: false,
        TextPrimary: "#1C2430",
        TextMuted: "#425166",
        LineColor: "#5F6E82",
        ClusterBackground: "#F3F6FB",
        ClusterBorder: "#CAD3E1",
        Primary: new MermaidPaletteEntry("#DCE8FF", "#7296E6", "#1C2430"),
        Success: new MermaidPaletteEntry("#DDF4E7", "#5CA878", "#1C2430"),
        Warning: new MermaidPaletteEntry("#FFF0CC", "#CC9A2C", "#1C2430"),
        Danger: new MermaidPaletteEntry("#F9D8E0", "#D07A95", "#1C2430"),
        Neutral: new MermaidPaletteEntry("#E8EDF5", "#A5B2C5", "#425166"));

    public static string Apply(string? source)
        => Apply(source, isDarkTheme: null);

    internal static string Apply(string? source, bool? isDarkTheme)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return string.Empty;
        }

        var text = source.Replace("\r\n", "\n").Trim();
        if (text.Contains(AppliedMarker, StringComparison.Ordinal))
        {
            return text;
        }

        var palette = ResolvePalette(isDarkTheme);
        var isFlowchart = FlowchartHeaderRegex.IsMatch(text);
        var classSlots = new Dictionary<string, MermaidPaletteSlot>(StringComparer.Ordinal);
        var nodeSlots = new Dictionary<string, MermaidPaletteSlot>(StringComparer.Ordinal);
        var nodeShapes = new Dictionary<string, MermaidNodeShape>(StringComparer.Ordinal);
        var pendingClassAssignments = new List<(string[] NodeIds, string[] ClassNames)>();
        var normalizedLines = new List<string>();

        foreach (var rawLine in text.Split('\n'))
        {
            if (TryNormalizeStyleLine(rawLine, palette, nodeSlots, out var normalizedStyleLine))
            {
                normalizedLines.Add(normalizedStyleLine);
                continue;
            }

            if (TryNormalizeClassDefLine(rawLine, palette, classSlots, out var normalizedClassDefLine))
            {
                normalizedLines.Add(normalizedClassDefLine);
                continue;
            }

            if (TryCaptureClassAssignments(rawLine, pendingClassAssignments))
            {
                normalizedLines.Add(rawLine.Trim());
                continue;
            }

            if (isFlowchart)
            {
                CaptureNodeShapes(rawLine, nodeShapes);
            }

            normalizedLines.Add(rawLine.TrimEnd());
        }

        foreach (var assignment in pendingClassAssignments)
        {
            var slot = ResolveAssignedClassSlot(assignment.ClassNames, classSlots);
            if (!slot.HasValue)
            {
                continue;
            }

            foreach (var nodeId in assignment.NodeIds)
            {
                nodeSlots[nodeId] = slot.Value;
            }
        }

        normalizedLines.Add(string.Empty);
        normalizedLines.Add(AppliedMarker);

        if (isFlowchart)
        {
            foreach (var (nodeId, nodeShape) in nodeShapes)
            {
                var slot = nodeSlots.TryGetValue(nodeId, out var assignedSlot)
                    ? assignedSlot
                    : GetDefaultSlot(nodeShape);
                normalizedLines.Add($"style {nodeId} {BuildPaletteProperties(slot, palette)}");
            }
        }

        return string.Join('\n', normalizedLines).Trim();
    }

    public static string CreateThemeVariablesJson(double fontSize)
    {
        var palette = ResolvePalette(isDarkTheme: null);
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fontSize"] = string.Create(CultureInfo.InvariantCulture, $"{fontSize:0.#}px"),
            ["primaryColor"] = palette.Primary.Fill,
            ["secondaryColor"] = palette.Success.Fill,
            ["tertiaryColor"] = palette.Neutral.Fill,
            ["primaryBorderColor"] = palette.Primary.Stroke,
            ["secondaryBorderColor"] = palette.Success.Stroke,
            ["tertiaryBorderColor"] = palette.Neutral.Stroke,
            ["primaryTextColor"] = palette.TextPrimary,
            ["secondaryTextColor"] = palette.TextPrimary,
            ["tertiaryTextColor"] = palette.TextMuted,
            ["lineColor"] = palette.LineColor,
            ["clusterBkg"] = palette.ClusterBackground,
            ["clusterBorder"] = palette.ClusterBorder,
            ["mainBkg"] = palette.IsDark ? "#11161F" : "#FFFFFF",
            ["nodeBorder"] = palette.Primary.Stroke,
            ["defaultLinkColor"] = palette.LineColor,
            ["edgeLabelBackground"] = palette.IsDark ? "#171D25" : "#FFFFFF"
        };

        return JsonSerializer.Serialize(variables);
    }

    private static MermaidPalette ResolvePalette(bool? isDarkTheme)
    {
        if (isDarkTheme.HasValue)
        {
            return isDarkTheme.Value ? DarkPalette : LightPalette;
        }

        if (Application.Current?.Resources[ThemeResourceKeys.PanelBackgroundBrush] is SolidColorBrush brush)
        {
            return GetRelativeLuminance(brush.Color) < 0.45 ? DarkPalette : LightPalette;
        }

        return DarkPalette;
    }

    private static bool TryNormalizeStyleLine(
        string line,
        MermaidPalette palette,
        Dictionary<string, MermaidPaletteSlot> nodeSlots,
        out string normalizedLine)
    {
        var match = StyleLineRegex.Match(line);
        if (!match.Success)
        {
            normalizedLine = string.Empty;
            return false;
        }

        var nodeIds = SplitIdentifiers(match.Groups["ids"].Value);
        var slot = ResolveSlotFromStyle(match.Groups["props"].Value, MermaidPaletteSlot.Primary);
        var properties = BuildMergedProperties(match.Groups["props"].Value, slot, palette);
        foreach (var nodeId in nodeIds)
        {
            nodeSlots[nodeId] = slot;
        }

        normalizedLine = $"style {string.Join(',', nodeIds)} {properties}";
        return true;
    }

    private static bool TryNormalizeClassDefLine(
        string line,
        MermaidPalette palette,
        Dictionary<string, MermaidPaletteSlot> classSlots,
        out string normalizedLine)
    {
        var match = ClassDefLineRegex.Match(line);
        if (!match.Success)
        {
            normalizedLine = string.Empty;
            return false;
        }

        var classNames = SplitIdentifiers(match.Groups["names"].Value);
        var slot = ResolveSlotFromStyle(match.Groups["props"].Value, MermaidPaletteSlot.Neutral);
        var properties = BuildMergedProperties(match.Groups["props"].Value, slot, palette);
        foreach (var className in classNames)
        {
            classSlots[className] = slot;
        }

        normalizedLine = $"classDef {string.Join(',', classNames)} {properties}";
        return true;
    }

    private static bool TryCaptureClassAssignments(string line, List<(string[] NodeIds, string[] ClassNames)> assignments)
    {
        var match = ClassAssignmentLineRegex.Match(line);
        if (!match.Success)
        {
            return false;
        }

        var nodeIds = SplitIdentifiers(match.Groups["ids"].Value);
        var classNames = SplitIdentifiers(match.Groups["classes"].Value);
        assignments.Add((nodeIds, classNames));
        return true;
    }

    private static MermaidPaletteSlot? ResolveAssignedClassSlot(
        IEnumerable<string> classNames,
        IReadOnlyDictionary<string, MermaidPaletteSlot> classSlots)
    {
        foreach (var className in classNames)
        {
            if (classSlots.TryGetValue(className, out var slot))
            {
                return slot;
            }
        }

        return null;
    }

    private static string[] SplitIdentifiers(string raw)
        => raw.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void CaptureNodeShapes(string line, Dictionary<string, MermaidNodeShape> nodeShapes)
    {
        foreach (Match match in NodeTokenRegex.Matches(line))
        {
            var nodeId = match.Groups["id"].Value.Trim();
            if (string.IsNullOrWhiteSpace(nodeId) || nodeShapes.ContainsKey(nodeId))
            {
                continue;
            }

            nodeShapes[nodeId] = match.Groups["shape"].Value switch
            {
                "{" => MermaidNodeShape.Decision,
                "{{" => MermaidNodeShape.Danger,
                "((" => MermaidNodeShape.Round,
                "(" => MermaidNodeShape.Round,
                "([" => MermaidNodeShape.Round,
                "[[" => MermaidNodeShape.Neutral,
                "[(" => MermaidNodeShape.Neutral,
                "[/" => MermaidNodeShape.Neutral,
                "[\\" => MermaidNodeShape.Neutral,
                _ => MermaidNodeShape.Rectangle
            };
        }
    }

    private static MermaidPaletteSlot ResolveSlotFromStyle(string properties, MermaidPaletteSlot fallback)
    {
        var map = ParsePropertyMap(properties);
        if (TryResolveSlotFromColor(map, "fill", out var slot)
            || TryResolveSlotFromColor(map, "stroke", out slot)
            || TryResolveSlotFromColor(map, "color", out slot))
        {
            return slot;
        }

        return fallback;
    }

    private static bool TryResolveSlotFromColor(IReadOnlyDictionary<string, string> propertyMap, string key, out MermaidPaletteSlot slot)
    {
        if (propertyMap.TryGetValue(key, out var value) && TryParseColor(value, out var color))
        {
            slot = MapColorToPaletteSlot(color);
            return true;
        }

        slot = MermaidPaletteSlot.Primary;
        return false;
    }

    private static string BuildMergedProperties(string source, MermaidPaletteSlot slot, MermaidPalette palette)
    {
        var propertyMap = ParsePropertyMap(source);
        propertyMap["fill"] = palette[slot].Fill;
        propertyMap["stroke"] = palette[slot].Stroke;
        propertyMap["color"] = palette[slot].Text;

        return string.Join(",",
            propertyMap.Select(static pair => $"{pair.Key}:{pair.Value}"));
    }

    private static Dictionary<string, string> ParsePropertyMap(string source)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in source.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = segment.IndexOf(':');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = segment[..separatorIndex].Trim();
            var value = segment[(separatorIndex + 1)..].Trim().TrimEnd(';');
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                properties[key] = value;
            }
        }

        return properties;
    }

    private static string BuildPaletteProperties(MermaidPaletteSlot slot, MermaidPalette palette)
        => $"fill:{palette[slot].Fill},stroke:{palette[slot].Stroke},color:{palette[slot].Text}";

    private static MermaidPaletteSlot GetDefaultSlot(MermaidNodeShape nodeShape)
        => nodeShape switch
        {
            MermaidNodeShape.Decision => MermaidPaletteSlot.Warning,
            MermaidNodeShape.Danger => MermaidPaletteSlot.Danger,
            MermaidNodeShape.Round => MermaidPaletteSlot.Success,
            MermaidNodeShape.Neutral => MermaidPaletteSlot.Neutral,
            _ => MermaidPaletteSlot.Primary
        };

    private static MermaidPaletteSlot MapColorToPaletteSlot(Color color)
    {
        var saturation = GetSaturation(color);
        if (saturation < 0.16)
        {
            return MermaidPaletteSlot.Neutral;
        }

        var hue = GetHue(color);
        return hue switch
        {
            >= 25 and < 75 => MermaidPaletteSlot.Warning,
            >= 75 and < 170 => MermaidPaletteSlot.Success,
            >= 170 and < 290 => MermaidPaletteSlot.Primary,
            _ => MermaidPaletteSlot.Danger
        };
    }

    private static bool TryParseColor(string? raw, out Color color)
    {
        var value = raw?.Trim().Trim('"', '\'');
        if (string.IsNullOrWhiteSpace(value))
        {
            color = default;
            return false;
        }

        if (NamedColors.TryGetValue(value, out color))
        {
            return true;
        }

        if (value.StartsWith('#'))
        {
            var hex = value[1..];
            if (hex.Length == 3)
            {
                hex = string.Concat(hex.Select(static ch => new string(ch, 2)));
            }

            if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            {
                color = Color.FromRgb(
                    (byte)((rgb >> 16) & 0xFF),
                    (byte)((rgb >> 8) & 0xFF),
                    (byte)(rgb & 0xFF));
                return true;
            }
        }

        var rgbMatch = RgbColorRegex.Match(value);
        if (rgbMatch.Success
            && byte.TryParse(rgbMatch.Groups["r"].Value, out var r)
            && byte.TryParse(rgbMatch.Groups["g"].Value, out var g)
            && byte.TryParse(rgbMatch.Groups["b"].Value, out var b))
        {
            color = Color.FromRgb(r, g, b);
            return true;
        }

        color = default;
        return false;
    }

    private static double GetRelativeLuminance(Color color)
    {
        static double Normalize(byte channel)
        {
            var srgb = channel / 255d;
            return srgb <= 0.03928 ? srgb / 12.92 : Math.Pow((srgb + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Normalize(color.R))
             + (0.7152 * Normalize(color.G))
             + (0.0722 * Normalize(color.B));
    }

    private static double GetSaturation(Color color)
    {
        var max = Math.Max(color.R, Math.Max(color.G, color.B)) / 255d;
        var min = Math.Min(color.R, Math.Min(color.G, color.B)) / 255d;
        if (Math.Abs(max - min) < 0.0001)
        {
            return 0;
        }

        var lightness = (max + min) / 2;
        return lightness > 0.5
            ? (max - min) / (2 - max - min)
            : (max - min) / (max + min);
    }

    private static double GetHue(Color color)
    {
        var r = color.R / 255d;
        var g = color.G / 255d;
        var b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        if (delta <= 0.0001)
        {
            return 0;
        }

        double hue;
        if (Math.Abs(max - r) < 0.0001)
        {
            hue = ((g - b) / delta) % 6;
        }
        else if (Math.Abs(max - g) < 0.0001)
        {
            hue = ((b - r) / delta) + 2;
        }
        else
        {
            hue = ((r - g) / delta) + 4;
        }

        hue *= 60;
        return hue < 0 ? hue + 360 : hue;
    }

    private enum MermaidPaletteSlot
    {
        Primary,
        Success,
        Warning,
        Danger,
        Neutral
    }

    private enum MermaidNodeShape
    {
        Rectangle,
        Round,
        Decision,
        Danger,
        Neutral
    }

    private sealed record MermaidPalette(
        bool IsDark,
        string TextPrimary,
        string TextMuted,
        string LineColor,
        string ClusterBackground,
        string ClusterBorder,
        MermaidPaletteEntry Primary,
        MermaidPaletteEntry Success,
        MermaidPaletteEntry Warning,
        MermaidPaletteEntry Danger,
        MermaidPaletteEntry Neutral)
    {
        public MermaidPaletteEntry this[MermaidPaletteSlot slot]
            => slot switch
            {
                MermaidPaletteSlot.Success => Success,
                MermaidPaletteSlot.Warning => Warning,
                MermaidPaletteSlot.Danger => Danger,
                MermaidPaletteSlot.Neutral => Neutral,
                _ => Primary
            };
    }

    private sealed record MermaidPaletteEntry(string Fill, string Stroke, string Text);
}


