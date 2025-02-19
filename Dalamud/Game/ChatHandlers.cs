using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using CheapLoc;

using Dalamud.Configuration.Internal;
using Dalamud.Game.Gui;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Internal;
using Dalamud.Logging.Internal;
using Dalamud.Plugin.Internal;
using Dalamud.Support;
using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace Dalamud.Game;

/// <summary>
/// Chat events and public helper functions.
/// </summary>
[ServiceManager.EarlyLoadedService]
internal partial class ChatHandlers : IServiceType
{
    private static readonly ModuleLog Log = new("ChatHandlers");

    private readonly Regex rmtRegex = new(
            @"没打开的[\dwW,]+收|登录领取.*福袋",
            // @"4KGOLD|We have sufficient stock|VPK\.OM|Gil for free|www\.so9\.com|Fast & Convenient|Cheap & Safety Guarantee|【Code|A O A U E|igfans|4KGOLD\.COM|Cheapest Gil with|pvp and bank on google|Selling Cheap GIL|ff14mogstation\.com|Cheap Gil 1000k|gilsforyou|server 1000K =|gils_selling|E A S Y\.C O M|bonus code|mins delivery guarantee|Sell cheap|Salegm\.com|cheap Mog|Off Code:|FF14Mog.com|使用する5％オ|Off Code( *):|offers Fantasia",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly Regex urlRegex = new(@"(http|ftp|https)://([\w_-]+(?:(?:\.[\w_-]+)+))([\w.,@?^=%&:/~+#-]*[\w@?^=%&/~+#-])?", RegexOptions.Compiled);

    [ServiceManager.ServiceDependency]
    private readonly Dalamud dalamud = Service<Dalamud>.Get();

    [ServiceManager.ServiceDependency]
    private readonly DalamudConfiguration configuration = Service<DalamudConfiguration>.Get();

    private bool hasSeenLoadingMsg;
    private CancellationTokenSource deferredAutoUpdateCts = new();

    [ServiceManager.ServiceConstructor]
    private ChatHandlers(ChatGui chatGui)
    {
        chatGui.CheckMessageHandled += this.OnCheckMessageHandled;
        chatGui.ChatMessage += this.OnChatMessage;
    }

    /// <summary>
    /// Gets the last URL seen in chat.
    /// </summary>
    public string? LastLink { get; private set; }

    /// <summary>
    /// Gets a value indicating whether or not auto-updates have already completed this session.
    /// </summary>
    public bool IsAutoUpdateComplete { get; private set; }

    [GeneratedRegex(@"(http|ftp|https)://([\w_-]+(?:(?:\.[\w_-]+)+))([\w.,@?^=%&:/~+#-]*[\w@?^=%&/~+#-])?", RegexOptions.Compiled)]
    private static partial Regex CompiledUrlRegex();

    private void OnCheckMessageHandled(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        var textVal = message.TextValue;

        if (this.configuration.BadWords != null &&
            this.configuration.BadWords.Any(x => !string.IsNullOrEmpty(x) && textVal.Contains(x)))
        {
            // This seems to be in the user block list - let's not show it
            Log.Debug("Filtered a message that contained a muted word");
            isHandled = true;
            return;
        }
    }

    private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        var clientState = Service<ClientState.ClientState>.GetNullable();
        if (clientState == null)
            return;

        if (type == XivChatType.Notice)
        {
            if (!this.hasSeenLoadingMsg)
                this.PrintWelcomeMessage();
        }

        // For injections while logged in
        if (clientState.LocalPlayer != null && clientState.TerritoryType == 0 && !this.hasSeenLoadingMsg)
            this.PrintWelcomeMessage();
#if !DEBUG && false
            if (!this.hasSeenLoadingMsg)
                return;
#endif

        var linkMatch = CompiledUrlRegex().Match(message.TextValue);
        if (linkMatch.Value.Length > 0)
            this.LastLink = linkMatch.Value;
    }

    private void PrintWelcomeMessage()
    {
        var chatGui = Service<ChatGui>.GetNullable();
        var pluginManager = Service<PluginManager>.GetNullable();
        var dalamudInterface = Service<DalamudInterface>.GetNullable();

        if (chatGui == null || pluginManager == null || dalamudInterface == null)
            return;

        var assemblyVersion = Assembly.GetAssembly(typeof(ChatHandlers)).GetName().Version.ToString();

        if (this.configuration.PrintDalamudWelcomeMsg)
        {
            chatGui.Print(string.Format(Loc.Localize("DalamudWelcome", "Dalamud {0} loaded."), Util.GetScmVersion())
                          + string.Format(Loc.Localize("PluginsWelcome", " {0} plugin(s) loaded."), pluginManager.InstalledPlugins.Count(x => x.IsLoaded)));
        }

        if (this.configuration.PrintPluginsWelcomeMsg)
        {
            foreach (var plugin in pluginManager.InstalledPlugins.OrderBy(plugin => plugin.Name).Where(x => x.IsLoaded))
            {
                chatGui.Print(string.Format(Loc.Localize("DalamudPluginLoaded", "    》 {0} v{1} loaded."), plugin.Name, plugin.EffectiveVersion));
            }
        }

        if (string.IsNullOrEmpty(this.configuration.LastVersion) || !assemblyVersion.StartsWith(this.configuration.LastVersion))
        {
            chatGui.Print(new XivChatEntry
            {
                Message = Loc.Localize("DalamudUpdated", "Dalamud has been updated successfully! Please check the discord for a full changelog."),
                Type = XivChatType.Notice,
            });

            this.configuration.LastVersion = assemblyVersion;
            this.configuration.QueueSave();
        }

        this.hasSeenLoadingMsg = true;

        Task.Run(() =>
        {
            try
            {
                Util.GetRemoteTOSHash().ContinueWith(task =>
                {
                    var remoteHash = task.Result;
                    if (string.IsNullOrEmpty(this.configuration.AcceptedTOSHash) || remoteHash != this.configuration.AcceptedTOSHash)
                    {
                        dalamudInterface.OpenToSWindow();
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Remote TOS hash check failed");
            }
        });

    }
}
