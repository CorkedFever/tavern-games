using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace TavernGames.Plugin.Windows;

/// <summary>
/// The stage: the cues a game fires when something happens, played out on the felt and the
/// seat over the next second or two, so a call, a bust or a win is seen rather than read.
/// <para>
/// Immediate-mode UI has no timeline, so every cue is a start time and a length, and whatever
/// draws it asks how far along it is this frame. Cues are fired from the message handlers and
/// drawn on the same thread (Dalamud runs both on the game's), so nothing here locks.
/// </para>
/// <para>
/// Stamps are the big words over the felt (LIAR!, BUST!, VICTORY). Rises are the small ones
/// that lift off a plate (+17, -1 die). Rolls make dice tumble before they settle; deals and
/// flips move cards. Glows pick out a plate, or the seat. Flights are chips crossing the felt.
/// Confetti is what it says.
/// </para>
/// </summary>
internal static class Fx
{
    private const double StampIn = 0.18;
    private const double StampOut = 0.45;
    private const double RiseLength = 1.6;
    private const double FlightLength = 0.6;
    private const double FlightStagger = 0.07;
    private const double DealLength = 0.28;
    private const double DealStagger = 0.12;
    private const double FlipLength = 0.45;
    private const double FlipStagger = 0.1;
    private const double VignetteLength = 1.4;

    /// <summary>A cue nobody drew for this long is stale: the window was closed, or the key was never drawn.</summary>
    private const double Stale = 20.0;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly Random Rng = new();

    private sealed record Stamp(string Word, Vector4 Colour, double Start, double Hold);

    private sealed record Rise(string Anchor, string Text, Vector4 Colour, double Start);

    private sealed record Flight(string To, Vector4 Colour, double Start, int Count);

    private sealed record GlowState(Vector4 Colour, double Start, double Length);

    private sealed record Chime(Action Play, double At);

    private struct Particle
    {
        public float X, Y, Speed, Sway, Phase, Size, Spin;
        public Vector4 Colour;
    }

    private static readonly List<Stamp> Stamps = new();
    private static readonly List<Rise> Rises = new();
    private static readonly List<Flight> Flights = new();
    private static readonly List<Chime> Cues = new();
    private static readonly Dictionary<string, (double Start, double Length)> Rolls = new();
    private static readonly Dictionary<string, (double Start, int From)> Deals = new();
    private static readonly Dictionary<string, double> Flips = new();
    private static readonly Dictionary<string, GlowState> Glows = new();

    private static Particle[]? _confetti;
    private static double _confettiStart;
    private static double _confettiLength;
    private static double _vignetteStart = double.NegativeInfinity;
    private static Vector4 _vignetteColour;

    private static readonly Vector4[] ConfettiColours =
    {
        Theme.Accent, Theme.Good, Theme.Rgb(0xF5, 0xF0, 0xDE), Theme.Rgb(0xA8, 0x3A, 0x3F), Theme.Rgb(0xFF, 0xFF, 0xFF),
    };

    public static double Now => Clock.Elapsed.TotalSeconds;

    /// <summary>The master switch, from the config. Off, cues are dropped as they are fired.</summary>
    public static bool Enabled { get; set; } = true;

    // -- firing cues -------------------------------------------------------------------------

    /// <summary>A big word over the felt: it punches in, holds, and drifts away.</summary>
    public static void StampFelt(string word, Vector4 colour, double hold = 1.4, double delay = 0)
    {
        if (Enabled)
            Stamps.Add(new Stamp(word.ToUpperInvariant(), colour, Now + delay, hold));
    }

    /// <summary>A small word lifting off a plate, or off the pot: +17, -1 die, all in.</summary>
    public static void Float(string anchor, string text, Vector4 colour, double delay = 0)
    {
        if (Enabled)
            Rises.Add(new Rise(anchor, text, colour, Now + delay));
    }

    /// <summary>Chips crossing the felt from the pot to a plate.</summary>
    public static void Fly(string toAnchor, int count, Vector4 colour, double delay = 0)
    {
        if (Enabled)
            Flights.Add(new Flight(toAnchor, colour, Now + delay, Math.Clamp(count, 1, 12)));
    }

    /// <summary>Dice under this key tumble for a while before they settle on what they show.</summary>
    public static void Roll(string key, double seconds = 0.8, double delay = 0)
    {
        if (Enabled)
            Rolls[key] = (Now + delay, seconds);
    }

    /// <summary>Whether dice under this key are still tumbling, and how far along the roll is.</summary>
    public static bool Rolling(string key, out float t)
    {
        t = 1f;
        if (!Rolls.TryGetValue(key, out var roll))
            return false;

        var elapsed = Now - roll.Start;
        if (elapsed < 0)
        {
            t = 0f;
            return true;
        }

        if (elapsed >= roll.Length)
        {
            Rolls.Remove(key);
            return false;
        }

        t = (float)(elapsed / roll.Length);
        return true;
    }

    /// <summary>Cards under this key arrive one after another, from the given index on. A new deal forgets any flip.</summary>
    public static void Deal(string key, int from = 0, double delay = 0)
    {
        Flips.Remove(key);
        if (Enabled)
            Deals[key] = (Now + delay, from);
    }

    /// <summary>How far a card has arrived: 0 still in the dealer's hand, 1 on the table.</summary>
    public static float DealProgress(string key, int index)
    {
        if (!Deals.TryGetValue(key, out var deal) || index < deal.From)
            return 1f;
        var elapsed = Now - deal.Start - (index - deal.From) * DealStagger;
        return (float)Math.Clamp(elapsed / DealLength, 0.0, 1.0);
    }

    /// <summary>Cards under this key turn over, one after another, starting now or a little later.</summary>
    public static void Flip(string key, double delay = 0)
    {
        if (Enabled)
            Flips[key] = Now + delay;
    }

    /// <summary>How far a card has turned: 0 still face down, 1 face up. 1 when nothing was asked to flip.</summary>
    public static float FlipProgress(string key, int index)
    {
        if (!Flips.TryGetValue(key, out var start))
            return 1f;
        var elapsed = Now - start - index * FlipStagger;
        return elapsed < 0 ? 0f : (float)Math.Min(1.0, elapsed / FlipLength);
    }

    /// <summary>Lights a plate (by player id) or the seat ("seat") in a colour for a moment.</summary>
    public static void Glow(string key, Vector4 colour, double seconds = 1.2, double delay = 0)
    {
        if (Enabled)
            Glows[key] = new GlowState(colour, Now + delay, seconds);
    }

    /// <summary>How brightly a key glows right now, 0 to 1, and in what colour.</summary>
    public static float GlowAmount(string key, out Vector4 colour)
    {
        colour = default;
        if (!Glows.TryGetValue(key, out var glow))
            return 0f;

        var elapsed = Now - glow.Start;
        if (elapsed < 0)
            return 0f;
        if (elapsed >= glow.Length)
        {
            Glows.Remove(key);
            return 0f;
        }

        colour = glow.Colour;
        var k = elapsed / glow.Length;
        return (float)(k < 0.15 ? k / 0.15 : 1.0 - (k - 0.15) / 0.85);
    }

    /// <summary>Confetti over the felt. Somebody won.</summary>
    public static void Confetti(double seconds = 4.0)
    {
        if (!Enabled)
            return;

        var particles = new Particle[90];
        for (var i = 0; i < particles.Length; i++)
        {
            particles[i] = new Particle
            {
                X = (float)Rng.NextDouble(),
                Y = -0.6f + 0.58f * (float)Rng.NextDouble(),
                Speed = 0.22f + 0.23f * (float)Rng.NextDouble(),
                Sway = 4f + 10f * (float)Rng.NextDouble(),
                Phase = (float)(Rng.NextDouble() * Math.PI * 2),
                Size = 3.5f + 3f * (float)Rng.NextDouble(),
                Spin = -6f + 12f * (float)Rng.NextDouble(),
                Colour = ConfettiColours[Rng.Next(ConfettiColours.Length)],
            };
        }

        _confetti = particles;
        _confettiStart = Now;
        _confettiLength = seconds;
    }

    /// <summary>The edges of the felt flush a colour and fade. Somebody else won.</summary>
    public static void Vignette(Vector4 colour)
    {
        if (!Enabled)
            return;
        _vignetteStart = Now;
        _vignetteColour = colour;
    }

    /// <summary>Something to do a little later: a chime once the dice have settled.</summary>
    public static void Cue(Action play, double delay) => Cues.Add(new Chime(play, Now + delay));

    public static void Reset()
    {
        Stamps.Clear();
        Rises.Clear();
        Flights.Clear();
        Cues.Clear();
        Rolls.Clear();
        Deals.Clear();
        Flips.Clear();
        Glows.Clear();
        _confetti = null;
        _vignetteStart = double.NegativeInfinity;
    }

    // -- playing them ------------------------------------------------------------------------

    /// <summary>Plays the cues that have come due. Once a frame, whether or not the felt is showing.</summary>
    public static void Tick()
    {
        var now = Now;
        for (var i = Cues.Count - 1; i >= 0; i--)
        {
            var cue = Cues[i];
            if (now < cue.At)
                continue;
            Cues.RemoveAt(i);

            // A cue that waited this long was fired while nobody was watching; playing it now
            // would be a chime about nothing.
            if (now - cue.At < 2.0)
                cue.Play();
        }

        // Keys nobody has drawn for a while (the window was closed) are dropped rather than kept.
        foreach (var key in Rolls.Where(r => now - r.Value.Start > Stale).Select(r => r.Key).ToList()) Rolls.Remove(key);
        foreach (var key in Deals.Where(d => now - d.Value.Start > Stale).Select(d => d.Key).ToList()) Deals.Remove(key);
        foreach (var key in Glows.Where(g => now - g.Value.Start > Stale).Select(g => g.Key).ToList()) Glows.Remove(key);
        Stamps.RemoveAll(s => now - s.Start > Stale);
        Rises.RemoveAll(r => now - r.Start > Stale);
        Flights.RemoveAll(f => now - f.Start > Stale);
    }

    /// <summary>
    /// Draws every playing cue over a rectangle, the felt usually. Anchors (a player id, "pot")
    /// are resolved through <paramref name="anchor"/>, which knows where the plates are.
    /// </summary>
    public static void DrawOverlay(ImDrawListPtr dl, Vector2 min, Vector2 max, Func<string, Vector2?> anchor)
    {
        var centre = (min + max) / 2f;
        DrawVignette(dl, min, max);
        DrawConfetti(dl, min, max);
        DrawFlights(dl, centre, anchor);
        DrawRises(dl, centre, anchor);
        DrawStamps(dl, min, max);
    }

    private static void DrawStamps(ImDrawListPtr dl, Vector2 min, Vector2 max)
    {
        if (Stamps.Count == 0)
            return;

        var now = Now;
        var centre = new Vector2((min.X + max.X) / 2f, min.Y + (max.Y - min.Y) * 0.42f);

        ImFontPtr font;
        float fontSize;
        var baseSizes = new Vector2[Stamps.Count];
        using (Theme.PushDisplayLarge())
        {
            font = ImGui.GetFont();
            fontSize = ImGui.GetFontSize();
            for (var i = 0; i < Stamps.Count; i++)
                baseSizes[i] = ImGui.CalcTextSize(Stamps[i].Word);
        }

        var row = 0;
        for (var i = 0; i < Stamps.Count; i++)
        {
            var stamp = Stamps[i];
            var elapsed = now - stamp.Start;
            if (elapsed < 0)
                continue;

            if (elapsed > StampIn + stamp.Hold + StampOut)
            {
                Stamps.RemoveAt(i);
                i--;
                continue;
            }

            float scale, alpha, drift = 0f;
            if (elapsed < StampIn)
            {
                var k = (float)(elapsed / StampIn);
                scale = 1.6f - 0.6f * EaseOut(k);
                alpha = k;
            }
            else if (elapsed < StampIn + stamp.Hold)
            {
                scale = 1f;
                alpha = 1f;
            }
            else
            {
                var k = (float)((elapsed - StampIn - stamp.Hold) / StampOut);
                scale = 1f + 0.06f * k;
                alpha = 1f - k;
                drift = -18f * k;
            }

            var size = fontSize * scale;
            var textSize = baseSizes[i] * scale;
            var pos = centre - textSize / 2f + new Vector2(0f, drift + row * 52f);
            var pad = new Vector2(18f, 8f);

            dl.AddRectFilled(pos - pad, pos + textSize + pad, Theme.U32(Theme.WithAlpha(Theme.Shell, 0.8f * alpha)), 8f);
            dl.AddRect(pos - pad, pos + textSize + pad, Theme.U32(Theme.WithAlpha(stamp.Colour, alpha)), 8f, ImDrawFlags.RoundCornersAll, 1.5f);
            dl.AddText(font, size, pos + new Vector2(2f, 2f), Theme.U32(new Vector4(0f, 0f, 0f, 0.6f * alpha)), stamp.Word);
            dl.AddText(font, size, pos, Theme.U32(Theme.WithAlpha(stamp.Colour, alpha)), stamp.Word);
            row++;
        }
    }

    private static void DrawRises(ImDrawListPtr dl, Vector2 centre, Func<string, Vector2?> anchor)
    {
        if (Rises.Count == 0)
            return;

        var now = Now;
        ImFontPtr font;
        float fontSize;
        var sizes = new Vector2[Rises.Count];
        using (Theme.PushDisplay())
        {
            font = ImGui.GetFont();
            fontSize = ImGui.GetFontSize();
            for (var i = 0; i < Rises.Count; i++)
                sizes[i] = ImGui.CalcTextSize(Rises[i].Text);
        }

        for (var i = 0; i < Rises.Count; i++)
        {
            var rise = Rises[i];
            var elapsed = now - rise.Start;
            if (elapsed < 0)
                continue;

            if (elapsed > RiseLength)
            {
                Rises.RemoveAt(i);
                i--;
                continue;
            }

            var at = anchor(rise.Anchor) ?? centre;
            var k = (float)(elapsed / RiseLength);
            var alpha = MathF.Min(1f, (float)(elapsed / 0.12)) * (k < 0.7f ? 1f : 1f - (k - 0.7f) / 0.3f);
            var y = at.Y - 6f - 36f * EaseOut(k);
            var pos = new Vector2(at.X - sizes[i].X / 2f, y - sizes[i].Y);
            var pad = new Vector2(8f, 2f);

            dl.AddRectFilled(pos - pad, pos + sizes[i] + pad, Theme.U32(Theme.WithAlpha(Theme.Shell, 0.75f * alpha)), 6f);
            dl.AddText(font, fontSize, pos, Theme.U32(Theme.WithAlpha(rise.Colour, alpha)), rise.Text);
        }
    }

    private static void DrawFlights(ImDrawListPtr dl, Vector2 centre, Func<string, Vector2?> anchor)
    {
        if (Flights.Count == 0)
            return;

        var now = Now;
        for (var i = 0; i < Flights.Count; i++)
        {
            var flight = Flights[i];
            var from = anchor("pot") ?? centre;
            var to = anchor(flight.To) ?? centre;
            var done = true;

            for (var c = 0; c < flight.Count; c++)
            {
                var elapsed = now - flight.Start - c * FlightStagger;
                if (elapsed < 0)
                {
                    done = false;
                    continue;
                }

                var k = (float)Math.Min(1.0, elapsed / FlightLength);
                if (k < 1f)
                    done = false;
                else
                    continue;

                var p = Vector2.Lerp(from, to, EaseInOut(k)) + new Vector2(0f, -MathF.Sin(k * MathF.PI) * 36f);
                dl.AddCircleFilled(p, 5.5f, Theme.U32(flight.Colour), 14);
                dl.AddCircle(p, 5.5f, Theme.U32(Theme.WithAlpha(Theme.Shell, 0.8f)), 14, 1.2f);
                dl.AddCircle(p, 2.6f, Theme.U32(Theme.WithAlpha(Theme.Rgb(0xF5, 0xF0, 0xDE), 0.9f)), 10, 1f);
            }

            if (done)
            {
                Flights.RemoveAt(i);
                i--;
            }
        }
    }

    private static void DrawConfetti(ImDrawListPtr dl, Vector2 min, Vector2 max)
    {
        if (_confetti is not { } particles)
            return;

        var elapsed = Now - _confettiStart;
        if (elapsed > _confettiLength)
        {
            _confetti = null;
            return;
        }

        var fade = (float)Math.Clamp((_confettiLength - elapsed) / 0.8, 0.0, 1.0);
        var width = max.X - min.X;
        var height = max.Y - min.Y;
        var t = (float)elapsed;

        foreach (var p in particles)
        {
            var y = min.Y + (p.Y + p.Speed * t) * height;
            if (y > max.Y + 10f || y < min.Y - 10f)
                continue;

            var x = min.X + p.X * width + MathF.Sin(t * 2f + p.Phase) * p.Sway;
            var angle = p.Spin * t + p.Phase;
            var cos = MathF.Cos(angle);
            var sin = MathF.Sin(angle);
            var hx = new Vector2(cos, sin) * p.Size;
            var hy = new Vector2(-sin, cos) * p.Size * 0.55f;
            var c = new Vector2(x, y);
            dl.AddQuadFilled(c - hx - hy, c + hx - hy, c + hx + hy, c - hx + hy, Theme.U32(Theme.WithAlpha(p.Colour, 0.95f * fade)));
        }
    }

    private static void DrawVignette(ImDrawListPtr dl, Vector2 min, Vector2 max)
    {
        var elapsed = Now - _vignetteStart;
        if (elapsed < 0 || elapsed > VignetteLength)
            return;

        var alpha = 0.5f * (1f - (float)(elapsed / VignetteLength));
        dl.AddRect(min, max, Theme.U32(Theme.WithAlpha(_vignetteColour, alpha)), 16f, ImDrawFlags.RoundCornersAll, 30f);
    }

    private static float EaseOut(float k) => 1f - MathF.Pow(1f - k, 3f);

    private static float EaseInOut(float k) => k < 0.5f ? 4f * k * k * k : 1f - MathF.Pow(-2f * k + 2f, 3f) / 2f;
}
