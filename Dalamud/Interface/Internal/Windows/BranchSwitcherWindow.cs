using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http.Json;
using System.Threading.Tasks;

using Dalamud.Configuration.Internal;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Networking.Http;
using Dalamud.Utility;

using ImGuiNET;
using Newtonsoft.Json;

namespace Dalamud.Interface.Internal.Windows;

/// <summary>
/// Window responsible for switching Dalamud beta branches.
/// </summary>
public class BranchSwitcherWindow : Window
{
    private const string BranchInfoUrlGlobal = "https://kamori.goats.dev/Dalamud/Release/Meta";
    private const string BranchInfoUrlCN = "https://aonyx.ffxiv.wang/Dalamud/Release/Meta";

    private string currentUrl = BranchInfoUrlGlobal;
    
    private Dictionary<string, VersionEntry> branches = [];
    private int selectedBranchIndex;

    /// <summary>
    /// Initializes a new instance of the <see cref="BranchSwitcherWindow"/> class.
    /// </summary>
    public BranchSwitcherWindow()
        : base("Branch Switcher", ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.ShowCloseButton = true;
        this.RespectCloseHotkey = true;
    }

    /// <inheritdoc/>
    public override void OnOpen()
    {
        this.ReloadBranchInfo();
        base.OnOpen();
    }

    /// <inheritdoc/>
    public override void Draw()
    {
        if (ImGui.Button("加载国际服分支"))
            ReloadBranchInfo();
        
        ImGui.SameLine();
        if (ImGui.Button("加载国服分支"))
            ReloadBranchInfo(BranchInfoUrlCN);
        
        var itemsArray = this.branches.Select(x => x.Key).ToArray();
        ImGui.ListBox("###Branch", ref this.selectedBranchIndex, itemsArray, itemsArray.Length);

        var pickedBranch = this.branches.ElementAt(this.selectedBranchIndex);

        ImGui.Text($"版本: {pickedBranch.Value.AssemblyVersion} ({pickedBranch.Value.GitSha ?? "unk"})");
        ImGui.Text($"运行时: {pickedBranch.Value.RuntimeVersion}");

        ImGuiHelpers.ScaledDummy(5);

        if (ImGui.Button("选择"))
        {
            Pick();
            this.IsOpen = false;
        }

        ImGui.SameLine();

        if (ImGui.Button("选择 & 重启"))
        {
            Pick();

            // If we exit immediately, we need to write out the new config now
            Service<DalamudConfiguration>.Get().ForceSave();

            var appData = Service<Dalamud>.Get().StartInfo.LauncherDirectory ?? string.Empty;
            var xlPath = Path.Combine(appData, "XIVLauncherCN.exe");

            if (File.Exists(xlPath))
            {
                Process.Start(xlPath);
                Environment.Exit(0);
            }
        }

        return;

        void Pick()
        {
            var config = Service<DalamudConfiguration>.Get();
            config.DalamudBetaKind = pickedBranch.Key;
            config.DalamudBetaKey  = pickedBranch.Value.Key;
            config.QueueSave();
        }
    }

    private void ReloadBranchInfo(string url = BranchInfoUrlGlobal)
    {
        Task.Run(async () =>
        {
            var client = Service<HappyHttpClient>.Get().SharedHttpClient;
            this.branches = await client.GetFromJsonAsync<Dictionary<string, VersionEntry>>(url);

            var config = Service<DalamudConfiguration>.Get();
            this.selectedBranchIndex = this.branches!.Any(x => x.Key          == config.DalamudBetaKind) ?
                                           this.branches.TakeWhile(x => x.Key != config.DalamudBetaKind).Count()
                                           : 0;

            if (this.branches.ElementAt(this.selectedBranchIndex).Value.Key != config.DalamudBetaKey)
                this.selectedBranchIndex = 0;
        });
    }

    private class VersionEntry
    {
        [JsonProperty("key")]
        public string? Key { get; set; }

        [JsonProperty("track")]
        public string? Track { get; set; }

        [JsonProperty("assemblyVersion")]
        public string? AssemblyVersion { get; set; }

        [JsonProperty("runtimeVersion")]
        public string? RuntimeVersion { get; set; }

        [JsonProperty("runtimeRequired")]
        public bool RuntimeRequired { get; set; }

        [JsonProperty("supportedGameVer")]
        public string? SupportedGameVer { get; set; }

        [JsonProperty("downloadUrl")]
        public string? DownloadUrl { get; set; }

        [JsonProperty("gitSha")]
        public string? GitSha { get; set; }
    }
}
