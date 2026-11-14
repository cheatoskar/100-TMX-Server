using System.Globalization;
using System.Text.RegularExpressions;

namespace RandomizerAnywhere;

// The HUD is written as TMF manialinks (version 1: posn/sizen, a screen of 128 x 96 units around
// the centre, stretched to the full screen width). TM2 and TM2020 draw version 3: pos/size,
// 320 x 180 units, z as its own z-index. Rather than a second HUD, every page is converted on its
// way out (RemoteClient.Manialink): the same spot on screen is 2.5x across and 1.875x down.
//
// TM2020 dropped most of the old background styles, so styled backgrounds become plain colour
// there; icons and medals stay styled (if one is missing it is just not drawn).
internal static partial class ManialinkV3
{
    public const double ScaleX = 320.0 / 128.0;
    public const double ScaleY = 180.0 / 96.0;

    public static string Convert(string tmf, bool tm2020)
    {
        var page = ManialinkOpenRegex().Replace(tmf, m =>
        {
            var tag = VersionAttributeRegex().Replace(m.Value, string.Empty);
            var end = tag.EndsWith("/>", StringComparison.Ordinal) ? tag.Length - 2 : tag.Length - 1;
            return tag.Insert(end, " version=\"3\"");
        });

        page = PosnRegex().Replace(page, m =>
        {
            var parts = Numbers(m.Groups["v"].Value);
            var x = parts.Length > 0 ? parts[0] * ScaleX : 0;
            var y = parts.Length > 1 ? parts[1] * ScaleY : 0;
            var z = parts.Length > 2 ? parts[2] : 0;
            return $" pos=\"{F(x)} {F(y)}\" z-index=\"{F(z)}\"";
        });

        page = SizenRegex().Replace(page, m =>
        {
            var parts = Numbers(m.Groups["v"].Value);
            var w = parts.Length > 0 ? parts[0] * ScaleX : 0;
            var h = parts.Length > 1 ? parts[1] * ScaleY : 0;
            return $" size=\"{F(w)} {F(h)}\"";
        });

        // TMF binds F-keys with actionkey; version 3 has no such attribute
        page = ActionKeyRegex().Replace(page, string.Empty);

        if (tm2020)
        {
            page = BackgroundStyleRegex().Replace(page, m => $" bgcolor=\"{BackgroundColour(m.Groups["sub"].Value)}\"");
        }

        return page;
    }

    private static string BackgroundColour(string substyle) => substyle switch
    {
        "BgRacePlayerName" => "0009",
        "ProgressBar" => "2A6C",
        "NavButton" => "333C",
        _ => "222C",
    };

    private static double[] Numbers(string value) =>
        value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0)
            .ToArray();

    private static string F(double value) => Math.Round(value, 3).ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex("<manialink(?=[\\s>])[^>]*>")]
    private static partial Regex ManialinkOpenRegex();

    [GeneratedRegex("\\s+version=\"[^\"]*\"")]
    private static partial Regex VersionAttributeRegex();

    [GeneratedRegex("\\s+posn=\"(?<v>[^\"]*)\"")]
    private static partial Regex PosnRegex();

    [GeneratedRegex("\\s+sizen=\"(?<v>[^\"]*)\"")]
    private static partial Regex SizenRegex();

    [GeneratedRegex("\\s+actionkey=\"[^\"]*\"")]
    private static partial Regex ActionKeyRegex();

    [GeneratedRegex("\\s+style=\"(?:BgsPlayerCard|Bgs1InRace|Bgs1)\"\\s+substyle=\"(?<sub>[^\"]*)\"")]
    private static partial Regex BackgroundStyleRegex();
}
