using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using TavernGames.Core.Protocol;
using TavernGames.Plugin.Game;
using TavernGames.Plugin.Games;
using TavernGames.Plugin.Windows;

namespace TavernGames.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/tavern";

    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    internal static ICommandManager CommandManager { get; private set; } = null!;
    internal static IPluginLog Log { get; private set; } = null!;
    internal static IFramework Framework { get; private set; } = null!;
    internal static IObjectTable ObjectTable { get; private set; } = null!;
    internal static IChatGui ChatGui { get; private set; } = null!;

    internal Configuration Config { get; }
    internal GameClient Client { get; }
    internal GameSession Session { get; } =
        new([new LiarsDiceClient(), new PigClient(), new BlackjackClient(), new HoldemClient()]);
    internal AccountState Account { get; } = new();

    private ConnectionState _lastConnectionState = ConnectionState.Disconnected;

    private readonly WindowSystem _windows = new("TavernGames");
    private readonly MainWindow _mainWindow;

    // Dalamud resolves these constructor parameters from its service container.
    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IPluginLog log,
        IFramework framework,
        IObjectTable objectTable,
        IChatGui chatGui)
    {
        PluginInterface = pluginInterface;
        CommandManager = commandManager;
        Log = log;
        Framework = framework;
        ObjectTable = objectTable;
        ChatGui = chatGui;

        try
        {
            log.Information("Tavern Games: constructor start");

            Config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            Client = new GameClient(log);

            _mainWindow = new MainWindow(this);
            _windows.AddWindow(_mainWindow);

            commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Open the Tavern Games window."
            });

            pluginInterface.UiBuilder.Draw += _windows.Draw;
            pluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;
            pluginInterface.UiBuilder.OpenConfigUi += ToggleMainWindow;
            framework.Update += OnFrameworkUpdate;

            log.Information("Tavern Games: constructor done");
        }
        catch (Exception ex)
        {
            // Fail loud and clean: tear down anything we registered, then let Dalamud
            // mark the load as failed rather than leaving the plugin half-initialized.
            log.Error(ex, "Tavern Games failed to initialize; cleaning up.");
            Dispose();
            throw;
        }
    }

    /// <summary>Drains queued server messages on the main thread so game state stays single-threaded.</summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            TrackConnection();

            while (Client.Inbound.TryDequeue(out var message))
            {
                if (Account.Apply(message))
                {
                    PersistAccountChanges();
                    continue;
                }

                Session.Apply(message);

                // Narrate the event to the local chat log (roleplay flavor, local-only).
                if (Config.NarrateToChat && ChatNarrator.BuildLine(message, Session) is { } line)
                {
                    try { ChatGui.Print(line); }
                    catch (Exception ex) { Log.Error(ex, "Tavern Games: chat narration failed."); }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Tavern Games: error applying a server message.");
        }
    }

    /// <summary>Says hello when a connection comes up, and forgets server state when it goes down.</summary>
    private void TrackConnection()
    {
        var state = Client.State;
        if (state == _lastConnectionState) return;

        if (state == ConnectionState.Connected)
        {
            SayHello();
        }
        else if (_lastConnectionState == ConnectionState.Connected)
        {
            // Whatever the old connection still had queued is about a table we've lost.
            while (Client.Inbound.TryDequeue(out _)) { }
            Session.Reset();
            Account.Reset();
        }

        _lastConnectionState = state;
    }

    /// <summary>Identifies this install to the server. The saved token (one per server) brings the same profile back.</summary>
    internal void SayHello()
    {
        Config.ProfileTokens.TryGetValue(Config.ServerUrl, out var token);
        Client.Send(new Hello(token, ResolvePlayerName()));
    }

    private void PersistAccountChanges()
    {
        if (Account.IssuedToken is { } token)
        {
            Account.IssuedToken = null;
            if (!Config.ProfileTokens.TryGetValue(Config.ServerUrl, out var saved) || saved != token)
            {
                Config.ProfileTokens[Config.ServerUrl] = token;
                Config.Save();
            }
        }

        if (Account.ProfileWasDeleted)
        {
            Account.ProfileWasDeleted = false;
            Config.ProfileTokens.Remove(Config.ServerUrl);
            Config.Save();
        }
    }

    /// <summary>The display name to send to the server: configured value, else the local character's name.</summary>
    internal string ResolvePlayerName()
    {
        if (!string.IsNullOrWhiteSpace(Config.PlayerName))
            return Config.PlayerName.Trim();
        return ObjectTable.LocalPlayer?.Name.TextValue ?? "Adventurer";
    }

    private void OnCommand(string command, string args) => ToggleMainWindow();

    private void ToggleMainWindow() => _mainWindow.Toggle();

    public void Dispose()
    {
        // Each step guarded so one failure can't block the rest of teardown.
        try { Framework.Update -= OnFrameworkUpdate; } catch (Exception ex) { Log.Error(ex, "unsub Framework.Update"); }
        try { PluginInterface.UiBuilder.Draw -= _windows.Draw; } catch (Exception ex) { Log.Error(ex, "unsub Draw"); }
        try { PluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow; } catch (Exception ex) { Log.Error(ex, "unsub OpenMainUi"); }
        try { PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainWindow; } catch (Exception ex) { Log.Error(ex, "unsub OpenConfigUi"); }
        try { _windows.RemoveAllWindows(); } catch (Exception ex) { Log.Error(ex, "RemoveAllWindows"); }
        try { CommandManager.RemoveHandler(CommandName); } catch (Exception ex) { Log.Error(ex, "RemoveHandler"); }
        try { Client?.Dispose(); } catch (Exception ex) { Log.Error(ex, "Client.Dispose"); }
    }
}
