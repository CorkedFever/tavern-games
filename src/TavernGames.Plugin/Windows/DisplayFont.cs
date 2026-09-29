using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace TavernGames.Plugin.Windows;

/// <summary>
/// Loads VT323, the teletext face, at two sizes and hands them out for the seat's display strip,
/// the keys, the headings and the marquee on the felt.
/// <para>
/// Never used for body text. A face drawn to look like a 1980s terminal has no hinting at small
/// sizes and no lowercase worth the name: fine for YOUR TURN, miserable for a sentence or an
/// error. Anything that might be long stays in the default face.
/// </para>
/// </summary>
internal sealed class DisplayFont : IDisposable
{
    private const string FileName = "VT323-Regular.ttf";

    private readonly IFontHandle? _small;
    private readonly IFontHandle? _large;

    public DisplayFont(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        try
        {
            var directory = pluginInterface.AssemblyLocation.Directory?.FullName;
            var path = Path.Combine(directory ?? ".", "Fonts", FileName);

            if (!File.Exists(path))
            {
                log.Warning("Tavern Games: {File} not found next to the plugin; using the default font.", FileName);
                return;
            }

            // VT323 is drawn on a 20-pixel grid, so it only looks right at that size or a
            // multiple of it. Anything in between blurs the pixel edges that are its character.
            _small = Load(pluginInterface, path, 20f);
            _large = Load(pluginInterface, path, 40f);
        }
        catch (Exception ex)
        {
            // A font that fails to load is cosmetic, not a reason to fail the plugin.
            log.Warning(ex, "Tavern Games: could not load the display font; using the default font.");
        }
    }

    public bool Available => _small is not null;

    /// <summary>Headings, keys, the display strip. A no-op scope when the font is missing.</summary>
    public IDisposable Push() => _small?.Push() ?? NullScope.Instance;

    /// <summary>The big words on the felt: a bid, a pot, a room code.</summary>
    public IDisposable PushLarge() => _large?.Push() ?? NullScope.Instance;

    public void Dispose()
    {
        _small?.Dispose();
        _large?.Dispose();
    }

    private static IFontHandle Load(IDalamudPluginInterface pluginInterface, string path, float sizePx) =>
        pluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(
            e => e.OnPreBuild(tk => tk.AddFontFromFile(path, new SafeFontConfig { SizePx = sizePx })));

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
