using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Host.Window;

namespace TenchuSARecomp;

// Nine glowing medallions turning slowly around a bright core, behind the launcher's own screens.
//
// It quotes the game's title screen rather than reproducing it. That screen is a pre-rendered
// texture: a blown-out sunburst with nine kanji discs ringed around it. Here there is no texture and
// no shader -- the core is a stack of filled circles at falling alpha, the discs are three circles
// each, and the whole thing is ImGui draw-list primitives. Cheap enough to run behind a menu nobody
// is looking directly at, and it scales to any window size because everything is derived from one
// radius.
//
// WHY NINE. They are the kuji, the nine-syllable mantra a ninja cuts in the air --
// rin pyo to sha kai jin retsu zai zen -- which is why the game's own screen has exactly nine and
// not eight or ten. The order below reads clockwise from the upper right, matching the screenshot.
//
// Drawn with draw-list primitives only, so it composes under ImGui content without any render-target
// work. It owns its timing off ImGui.GetIO().DeltaTime, so it animates for as long as frames pump.
public sealed class KujiBackground
{
    // The kuji, in mantra order. Rendering these needs a font that covers them; the launcher asks
    // for that via HostFonts.ExtraGlyphs and falls back to Latin when the machine has none, which is
    // why Glyphs() below takes the decision rather than this array being used directly.
    public const string Kuji = "臨兵闘者皆陣列在前";

    // Nine slots, so it fits the same ring without any special-casing. 1998 was the Japanese
    // release year, and the hyphen doubles as the separator the ring needs to look deliberate
    // rather than like a word that ran out of letters.
    public const string LatinFallback = "TENCHU-98";

    float _phase;

    /// <summary>Radians per second. Negative turns anticlockwise.</summary>
    public float Speed = 0.16f;

    // Warm amber, sampled by eye from the title screen: a near-white core, a saturated disc, and a
    // deep ember for the halo. Kept as three stops rather than one colour because a single amber at
    // varying alpha reads as muddy brown once it is over a dark field.
    static readonly Vector3 Core = new(1.00f, 0.97f, 0.86f);
    static readonly Vector3 Disc = new(1.00f, 0.66f, 0.16f);
    static readonly Vector3 Ember = new(0.85f, 0.32f, 0.05f);

    // The characters are dark ON the disc, as on the original -- not glowing text on a dark field.
    // Near-black rather than the brown it looks like on the screenshot: on a 30px medallion the
    // browner ink loses its strokes against the amber, and on the build screen these characters are
    // carrying a word the user is meant to READ rather than just decoration.
    static readonly Vector3 Ink = new(0.09f, 0.03f, 0.00f);

    /// <summary>
    /// The characters actually drawn: the kuji when the atlas can render them, Latin otherwise.
    /// Always nine long.
    /// </summary>
    public static string Glyphs() => HostFonts.ExtraGlyphsAvailable ? Kuji : LatinFallback;

    /// <summary>
    /// Draws the ring centred on <paramref name="centre"/>. <paramref name="radius"/> is the radius
    /// of the ring the medallions sit on, not of the whole effect -- the halo reaches beyond it.
    /// <paramref name="text"/> overrides the glyphs, for callers that want the ring to spell
    /// something; it is padded or trimmed to nine.
    /// </summary>
    public void Draw(ImDrawListPtr dl, Vector2 centre, float radius, float alpha = 1f,
                     string? text = null, ImFontPtr? font = null)
    {
        float dt = Math.Clamp(ImGui.GetIO().DeltaTime, 0f, 0.1f);
        _phase += dt * Speed;

        alpha = Math.Clamp(alpha, 0f, 1f);
        if (alpha <= 0.002f) return;

        Halo(dl, centre, radius, alpha);
        Medallions(dl, centre, radius, alpha, Fit(text ?? Glyphs()), font);
    }

    // Exactly nine characters, whatever the caller passed. Spaces are legitimate slots -- an empty
    // medallion still turns, which is the point of using the ring as a progress display.
    static string Fit(string s)
    {
        if (s.Length == 9) return s;
        if (s.Length > 9) return s[..9];
        return s.PadRight(9, ' ');
    }

    // The blown-out centre. Filled circles from wide-and-faint to tight-and-bright; the falloff is
    // quadratic so the bright part stays small instead of washing the whole screen, which is what a
    // linear ramp does here.
    void Halo(ImDrawListPtr dl, Vector2 c, float r, float a)
    {
        const int Steps = 22;
        for (int i = Steps; i >= 1; i--)
        {
            float t = i / (float)Steps;                 // 1 = outermost
            float rr = r * (0.30f + 1.55f * t);
            float k = 1f - t;                            // 0 outside, 1 at the core
            float intensity = k * k;
            var col = Vector3.Lerp(Ember, Core, intensity);
            dl.AddCircleFilled(c, rr, Col(col, 0.055f * intensity * a + 0.010f * a), 48);
        }
    }

    void Medallions(ImDrawListPtr dl, Vector2 c, float r, float a, string glyphs, ImFontPtr? font)
    {
        // Disc size is tied to the gap between neighbours, so nine of them always sit apart by the
        // same proportion no matter how large the ring is.
        float step = MathF.Tau / 9f;
        float rad = r * step * 0.40f;

        for (int i = 0; i < 9; i++)
        {
            // -PI/2 puts slot 0 at the top; the ring then reads clockwise, as the original does.
            float ang = _phase + i * step - MathF.PI / 2f;
            var p = new Vector2(c.X + MathF.Cos(ang) * r, c.Y + MathF.Sin(ang) * r);

            // Slight breathing, offset per medallion so the ring never pulses in unison.
            float pulse = 0.90f + 0.10f * MathF.Sin(_phase * 3.1f + i * 0.7f);
            float rr = rad * pulse;

            // Halo, body, rim. Three passes is enough to read as a glow at this size.
            for (int g = 4; g >= 1; g--)
                dl.AddCircleFilled(p, rr * (1f + g * 0.30f), Col(Ember, 0.05f * a), 32);
            dl.AddCircleFilled(p, rr, Col(Disc, 0.96f * a), 32);
            dl.AddCircleFilled(p, rr * 0.74f, Col(Vector3.Lerp(Disc, Core, 0.50f), 0.95f * a), 32);
            dl.AddCircle(p, rr, Col(Core, 0.55f * a), 32, Math.Max(1f, rr * 0.06f));

            char ch = glyphs[i];
            if (ch == ' ') continue;

            // Measured, not assumed: the fallback face and a CJK face have very different advance
            // widths for the same nominal size, so centring on a guess visibly misplaces one of them.
            string s = ch.ToString();
            if (font is { } f) ImGui.PushFont(f);
            var sz = ImGui.CalcTextSize(s);
            dl.AddText(new Vector2(p.X - sz.X * 0.5f, p.Y - sz.Y * 0.5f), Col(Ink, MathF.Min(1f, a * 1.6f)), s);
            if (font is not null) ImGui.PopFont();
        }
    }

    static uint Col(Vector3 rgb, float a) =>
        ImGui.ColorConvertFloat4ToU32(new Vector4(rgb.X, rgb.Y, rgb.Z, Math.Clamp(a, 0f, 1f)));
}
