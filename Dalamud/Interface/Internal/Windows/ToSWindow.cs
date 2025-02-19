using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Configuration.Internal;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using Dalamud.Networking.Http;
using ImGuiNET;
using ImGuiScene;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using Serilog;

namespace Dalamud.Interface.Internal.Windows;

/// <summary>
/// For major updates, an in-game Changelog window.
/// </summary>
internal sealed class ToSWindow : Window, IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ChangelogWindow"/> class.
    /// </summary>
    public ToSWindow()
        : base("Dalamud Terms of Service", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse)
    {
        this.IsOpen = false;
    }

    /// <inheritdoc/>
    public override void Draw()
    {
    }

    /// <summary>
    /// Dispose this window.
    /// </summary>
    public void Dispose()
    {
    }
}
