using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Configuration.Internal;
using Dalamud.Game;
using Dalamud.Interface.Internal;
using Dalamud.IoC.Internal;
using Dalamud.Logging.Internal;
using Dalamud.Plugin.Internal.Exceptions;
using Dalamud.Plugin.Internal.Loader;
using Dalamud.Plugin.Internal.Profiles;
using Dalamud.Plugin.Internal.Types.Manifest;

namespace Dalamud.Plugin.Internal.Types;

/// <summary>
/// 此类表示一个插件及其生命周期的所有方面。
/// 包括磁盘上的 DLL 文件、依赖项、加载的程序集等。
/// </summary>
internal class LocalPlugin : IAsyncDisposable
{
    /// <summary>
    /// 此插件的底层清单。
    /// </summary>
#pragma warning disable SA1401
    protected LocalPluginManifest manifest;
#pragma warning restore SA1401

    private static readonly ModuleLog Log = new("LOCALPLUGIN");

    private readonly FileInfo manifestFile;
    private readonly FileInfo disabledFile;
    private readonly FileInfo testingFile;

    private readonly SemaphoreSlim pluginLoadStateLock = new(1);

    private PluginLoader? loader;
    private Assembly? pluginAssembly;
    private Type? pluginType;
    private IDalamudPlugin? instance;
    private IServiceScope? serviceScope;
    private DalamudPluginInterface? dalamudInterface;

    /// <summary>
    /// 初始化 <see cref="LocalPlugin"/> 类的新实例。
    /// </summary>
    /// <param name="dllFile">DLL 文件的路径。</param>
    /// <param name="manifest">插件清单。</param>
    public LocalPlugin(FileInfo dllFile, LocalPluginManifest manifest)
    {
        if (dllFile.Name == "FFXIVClientStructs.Generators.dll")
        {
            // 虽然可以用其他方式实现，但这是日志中极其常见的错误来源，并且不应该作为插件加载。
            Log.Error($"不是插件: {dllFile.FullName}");
            throw new InvalidPluginException(dllFile);
        }

        this.DllFile = dllFile;
        this.State = PluginState.Unloaded;

        // 虽然这里是有条件使用的，但我们需要无论如何都设置初始值。
        this.manifestFile = LocalPluginManifest.GetManifestFile(this.DllFile);
        this.manifest = manifest;

        var needsSaveDueToLegacyFiles = false;

        // 这将 ".disabled" 文件功能转换为清单。
        this.disabledFile = LocalPluginManifest.GetDisabledFile(this.DllFile);
        if (this.disabledFile.Exists)
        {
#pragma warning disable CS0618
            this.manifest.Disabled = true;
#pragma warning restore CS0618
            this.disabledFile.Delete();

            needsSaveDueToLegacyFiles = true;
        }

        // 这将 ".testing" 文件功能转换为清单。
        this.testingFile = LocalPluginManifest.GetTestingFile(this.DllFile);
        if (this.testingFile.Exists)
        {
            this.manifest.Testing = true;
            this.testingFile.Delete();

            needsSaveDueToLegacyFiles = true;
        }

        // 为此插件创建一个安装实例 ID，如果它还没有的话
        if (this.manifest.WorkingPluginId == Guid.Empty && !this.IsDev)
        {
            this.manifest.WorkingPluginId = Guid.NewGuid();

            needsSaveDueToLegacyFiles = true;
        }

        var pluginManager = Service<PluginManager>.Get();
        this.IsBanned = pluginManager.IsManifestBanned(this.manifest); // && !this.IsDev;
        this.BanReason = pluginManager.GetBanReason(this.manifest);

        if (needsSaveDueToLegacyFiles)
            this.SaveManifest("legacy");
    }

    /// <summary>
    /// 获取与此插件关联的 <see cref="DalamudPluginInterface"/>。
    /// </summary>
    public DalamudPluginInterface? DalamudInterface => this.dalamudInterface;

    /// <summary>
    /// 获取插件 DLL 的路径。
    /// </summary>
    public FileInfo DllFile { get; }

    /// <summary>
    /// 获取插件清单。
    /// </summary>
    public ILocalPluginManifest Manifest => this.manifest;

    /// <summary>
    /// 获取或设置插件的当前状态。
    /// </summary>
    public PluginState State { get; protected set; }

    /// <summary>
    /// 获取插件的 AssemblyName，在 <see cref="LoadAsync"/> 期间填充。
    /// </summary>
    /// <returns>插件类型。</returns>
    public AssemblyName? AssemblyName { get; private set; }

    /// <summary>
    /// 从清单获取插件名称。
    /// </summary>
    public string Name => this.manifest.Name;

    /// <summary>
    /// 从清单获取插件内部名称。
    /// </summary>
    public string InternalName => this.manifest.InternalName;

    /// <summary>
    /// 获取一个可选的原因，如果插件被禁止。
    /// </summary>
    public string BanReason { get; }

    /// <summary>
    /// 获取一个值，表示插件是否曾经开始加载。
    /// </summary>
    public bool HasEverStartedLoad { get; private set; }

    /// <summary>
    /// 获取一个值，表示插件是否已加载并正在运行。
    /// </summary>
    public bool IsLoaded => this.State == PluginState.Loaded;

    /// <summary>
    /// 获取一个值，表示此插件是否被任何配置文件希望激活。
    /// 包括默认配置文件。
    /// </summary>
    public bool IsWantedByAnyProfile =>
        Service<ProfileManager>.Get().GetWantStateAsync(this.EffectiveWorkingPluginId, this.Manifest.InternalName, false, false).GetAwaiter().GetResult();

    /// <summary>
    /// 获取插件当前的 APILevel
    /// </summary>
    public int APILevel => this.manifest.EffectiveApiLevel;
    
    /// <summary>
    /// 获取一个值，表示此插件的 API 级别是否已过时。
    /// </summary>
    public bool IsOutdated => this.manifest.EffectiveApiLevel != PluginManager.DalamudApiLevel;

    /// <summary>
    /// 获取一个值，表示插件是否仅供测试使用。
    /// </summary>
    public bool IsTesting => this.manifest.IsTestingExclusive || this.manifest.Testing;

    /// <summary>
    /// 获取一个值，表示此插件是否为孤立插件（属于一个仓库）。
    /// </summary>
    public bool IsOrphaned => false;

    /// <summary>
    /// 获取一个值，表示此插件是否已退役（仓库仍然存在，但插件不再存在）。
    /// </summary>
    public bool IsDecommissioned => !this.IsDev &&
                                    this.GetSourceRepository()?.State == PluginRepositoryState.Success &&
                                    this.GetSourceRepository()?.PluginMaster?.FirstOrDefault(x => x.InternalName == this.manifest.InternalName) == null;

    /// <summary>
    /// 获取一个值，表示此插件是否已被禁止。
    /// </summary>
    public bool IsBanned { get; }

    /// <summary>
    /// 获取一个值，表示此插件是否为开发插件。
    /// </summary>
    public bool IsDev => this is LocalDevPlugin;

    /// <summary>
    /// 获取一个值，表示此清单是否与从第三方仓库安装的插件相关联。
    /// </summary>
    public bool IsThirdParty => this.manifest.IsThirdParty;

    /// <summary>
    /// 获取一个值，表示此插件是否应该被允许加载。
    /// </summary>
    public bool ApplicableForLoad => !this.IsBanned && !this.IsDecommissioned && !this.IsOrphaned && !this.IsOutdated
                                     && !(!this.IsDev && this.State == PluginState.UnloadError) && this.CheckPolicy();

    /// <summary>
    /// 获取此插件的有效版本。
    /// </summary>
    public Version EffectiveVersion => this.manifest.EffectiveVersion;

    /// <summary>
    /// 获取此插件的有效工作插件 ID。
    /// </summary>
    public virtual Guid EffectiveWorkingPluginId => this.manifest.WorkingPluginId;

    /// <summary>
    /// 获取此插件的服务作用域。
    /// </summary>
    public IServiceScope? ServiceScope => this.serviceScope;

    /// <inheritdoc/>
    public virtual async ValueTask DisposeAsync() =>
        await this.ClearAndDisposeAllResources(PluginLoaderDisposalMode.ImmediateDispose);

    /// <summary>
    /// 加载此插件。
    /// </summary>
    /// <param name="reason">加载此插件的原因。</param>
    /// <param name="reloading">是否在重新加载。</param>
    /// <returns>一个任务。</returns>
    public async Task LoadAsync(PluginLoadReason reason, bool reloading = false)
    {
        var ioc = await Service<ServiceContainer>.GetAsync();
        var pluginManager = await Service<PluginManager>.GetAsync();
        var dalamud = await Service<Dalamud>.GetAsync();

        if (this.manifest.LoadRequiredState == 0)
            _ = await Service<InterfaceManager.InterfaceManagerWithScene>.GetAsync();

        await this.pluginLoadStateLock.WaitAsync();
        try
        {
            if (reloading)
                this.OnPreReload();

            // 如果我们重新加载插件，我们不想删除它。有道理，对吧？
            if (this.manifest.ScheduledForDeletion)
            {
                this.manifest.ScheduledForDeletion = false;
                this.SaveManifest("计划删除，但正在加载");
            }

            switch (this.State)
            {
                case PluginState.Loaded:
                    throw new InvalidPluginOperationException($"无法加载 {this.Name}，已经加载");
                case PluginState.LoadError:
                    if (!this.IsDev)
                    {
                        throw new InvalidPluginOperationException(
                            $"无法加载 {this.Name}，加载先前失败，请先卸载");
                    }

                    break;
                case PluginState.UnloadError:
                    if (!this.IsDev)
                    {
                        throw new InvalidPluginOperationException(
                            $"无法加载 {this.Name}，卸载先前失败，请重启 Dalamud");
                    }

                    break;
                case PluginState.Unloaded:
                    if (this.instance is not null)
                    {
                        throw new InternalPluginStateException(
                            "插件应该已卸载但实例未清除");
                    }

                    break;
                case PluginState.Loading:
                case PluginState.Unloading:
                default:
                    throw new ArgumentOutOfRangeException(this.State.ToString());
            }

            // if (pluginManager.IsManifestBanned(this.Manifest) && !this.IsDev)
            if (pluginManager.IsManifestBanned(this.manifest))
                    throw new BannedPluginException($"无法加载 {this.Name}，已被禁止");

            if (this.manifest.ApplicableVersion < dalamud.StartInfo.GameVersion)
                throw new PluginPreconditionFailedException($"无法加载 {this.Name}，游戏版本新于适用版本 {this.manifest.ApplicableVersion}");

            // 我们希望允许加载 API 级别低于当前 Dalamud API 级别的开发插件，以便于开发
            if (APILevel != PluginManager.DalamudApiLevel && !pluginManager.LoadAllApiLevels && !this.IsDev)
                throw new PluginPreconditionFailedException($"无法加载 {this.Name}，不兼容的 API 级别 {this.manifest.EffectiveApiLevel}");

            // 我们可能想在这里抛出异常？
            if (!this.IsWantedByAnyProfile)
                Log.Warning("{Name} 正在加载，但不被任何配置文件需要", this.Name);

            if (this.IsOrphaned)
                throw new PluginPreconditionFailedException($"插件 {this.Name} 没有关联的仓库");

            if (!this.CheckPolicy())
                throw new PluginPreconditionFailedException($"由于加载策略禁止，无法加载 {this.Name}");

            this.State = PluginState.Loading;
            Log.Information($"正在加载 {this.DllFile.Name}");

            this.EnsureLoader();

            if (this.DllFile.DirectoryName != null &&
                File.Exists(Path.Combine(this.DllFile.DirectoryName, "Dalamud.dll")))
            {
                Log.Error(
                    "==== 给 {0}，{1} 的开发者的重要信息 ====",
                    this.manifest.Author!,
                    this.manifest.InternalName);
                Log.Error(
                    "你在构建中包含了 DALAMUD 依赖项!!!");
                Log.Error(
                    "你可能无法加载你的插件。需要在你的 csproj 中设置 \"<Private>False</Private>\"。");
                Log.Error(
                    "如果你使用 ILMerge，请不要合并除了你的直接依赖项以外的任何内容。");
                Log.Error("不要合并 FFXIVClientStructs.Generators.dll。");
                Log.Error(
                    "请参考 https://github.com/goatcorp/Dalamud/discussions/603 获取更多信息。");
            }

            this.HasEverStartedLoad = true;

            this.loader ??= PluginLoader.CreateFromAssemblyFile(this.DllFile.FullName, SetupLoaderConfig);

            if (reloading || this.IsDev)
            {
                if (this.IsDev)
                {
                    // 如果开发插件设置为不在启动时自动加载，但我们想在任意加载时间重新加载它，
                    // 我们需要实质上"卸载"插件，但由于加载状态检查，我们不能调用 plugin.Unload。
                    // 将对程序集和类型的任何引用置空，然后继续进行常规重新加载操作。
                    this.pluginAssembly = null;
                    this.pluginType = null;
                }

                this.loader.Reload();
            }

            // 加载程序集
            this.pluginAssembly ??= this.loader.LoadDefaultAssembly();

            this.AssemblyName = this.pluginAssembly.GetName();

            // 查找插件接口实现。在构造函数中检查后可以保证它存在。
            this.pluginType ??= this.pluginAssembly.GetTypes()
                                    .First(type => type.IsAssignableTo(typeof(IDalamudPlugin)));

            // 检查是否有任何已加载的插件具有相同的程序集名称
            var assemblyName = this.pluginAssembly.GetName().Name;
            foreach (var otherPlugin in pluginManager.InstalledPlugins)
            {
                // 在热重载期间，这个插件将在插件列表中，并且实例将已被处置
                if (otherPlugin == this || otherPlugin.instance == null)
                    continue;

                var otherPluginAssemblyName =
                    otherPlugin.instance.GetType().Assembly.GetName().Name;
                if (otherPluginAssemblyName == assemblyName && otherPluginAssemblyName != null)
                {
                    this.State = PluginState.Unloaded;
                    Log.Debug($"重复的程序集: {this.Name}");

                    throw new DuplicatePluginException(assemblyName);
                }
            }

            this.dalamudInterface = new(this, reason);

            this.serviceScope = ioc.GetScope();
            this.serviceScope.RegisterPrivateScopes(this); // 添加这个 LocalPlugin 作为私有作用域，以便服务可以获取它

            try
            {
                this.instance = await CreatePluginInstance(
                                    this.manifest,
                                    this.serviceScope,
                                    this.pluginType,
                                    this.dalamudInterface);
                this.State = PluginState.Loaded;
                Log.Information("完成加载 {PluginName}", this.InternalName);
            }
            catch (Exception ex)
            {
                this.State = PluginState.LoadError;
                Log.Error(
                    ex,
                    "加载 {PluginName} 时出错，绑定并调用插件构造函数失败",
                    this.InternalName);
                await this.ClearAndDisposeAllResources(PluginLoaderDisposalMode.ImmediateDispose);
            }
        }
        catch (Exception ex)
        {
            // 这些是"用户错误"，我们不想将插件标记为失败
            if (ex is not InvalidPluginOperationException)
                this.State = PluginState.LoadError;

            // 如果前提条件失败，不要将其记录为错误，因为它实际上不是错误。
            if (ex is PluginPreconditionFailedException)
                Log.Warning(ex.Message);
            else
                Log.Error(ex, "加载 {PluginName} 时出错", this.InternalName);

            throw;
        }
        finally
        {
            this.pluginLoadStateLock.Release();
        }
    }

    /// <summary>
    /// 卸载此插件。这与处置相同，但没有"已处置"的含义。此对象应保留在插件列表中，直到它被实际处置。
    /// </summary>
    /// <param name="disposalMode">如何处置加载器。</param>
    /// <returns>任务。</returns>
    public async Task UnloadAsync(PluginLoaderDisposalMode disposalMode = PluginLoaderDisposalMode.WaitBeforeDispose)
    {
        await this.pluginLoadStateLock.WaitAsync();
        try
        {
            switch (this.State)
            {
                case PluginState.Unloaded:
                    throw new InvalidPluginOperationException($"无法卸载 {this.Name}，已经卸载");
                case PluginState.DependencyResolutionFailed:
                case PluginState.UnloadError:
                    if (!this.IsDev)
                    {
                        throw new InvalidPluginOperationException(
                            $"无法卸载 {this.Name}，卸载先前失败，请重启 Dalamud");
                    }

                    break;
                case PluginState.Loaded:
                case PluginState.LoadError:
                    break;
                case PluginState.Loading:
                case PluginState.Unloading:
                default:
                    throw new ArgumentOutOfRangeException(this.State.ToString());
            }

            this.State = PluginState.Unloading;
            Log.Information("正在卸载 {PluginName}", this.InternalName);

            if (await this.ClearAndDisposeAllResources(disposalMode) is { } ex)
            {
                this.State = PluginState.UnloadError;
                throw ex;
            }

            this.State = PluginState.Unloaded;
            Log.Information("完成卸载 {PluginName}", this.InternalName);
        }
        catch (Exception ex)
        {
            // 这些是"用户错误"，我们不想将插件标记为失败
            if (ex is not InvalidPluginOperationException)
                this.State = PluginState.UnloadError;

            Log.Error(ex, "卸载 {PluginName} 时出错", this.InternalName);

            throw;
        }
        finally
        {
            this.pluginLoadStateLock.Release();
        }
    }

    /// <summary>
    /// 重新加载此插件。
    /// </summary>
    /// <returns>一个任务。</returns>
    public async Task ReloadAsync()
    {
        // 如果我们是开发插件并且有卸载错误，不要卸载，这是个坏主意，但无论如何
        if (this.IsDev && this.State != PluginState.UnloadError)
            await this.UnloadAsync(PluginLoaderDisposalMode.None);

        await this.LoadAsync(PluginLoadReason.Reload, true);
    }

    /// <summary>
    /// 检查是否有任何策略禁止此插件加载。
    /// </summary>
    /// <returns>此插件是否不应该加载。</returns>
    public bool CheckPolicy()
    {
        var startInfo = Service<Dalamud>.Get().StartInfo;
        var manager = Service<PluginManager>.Get();

        if (startInfo.NoLoadThirdPartyPlugins && this.manifest.IsThirdParty)
            return false;

        if (manager.SafeMode)
            return false;

        return true;
    }

    /// <summary>
    /// 计划在下次清理时删除此插件。
    /// </summary>
    /// <param name="status">计划或取消删除。</param>
    public void ScheduleDeletion(bool status = true)
    {
        this.manifest.ScheduledForDeletion = status;
        this.SaveManifest("计划删除");
    }

    /// <summary>
    /// 获取此插件安装来源的仓库。
    /// </summary>
    /// <returns>此插件安装来源的插件仓库，如果它不再存在或者插件是开发插件，则返回 null。</returns>
    public PluginRepository? GetSourceRepository()
    {
        if (this.IsDev)
            return null;

        var repos = Service<PluginManager>.Get().Repos;
        return repos.FirstOrDefault(x =>
        {
            if (!x.IsThirdParty && !this.manifest.IsThirdParty)
                return true;

            return x.PluginMasterUrl == this.manifest.InstalledFromUrl;
        });
    }

    /// <summary>
    /// 保存此插件清单。
    /// </summary>
    /// <param name="reason">为什么应该保存。</param>
    protected void SaveManifest(string reason) => this.manifest.Save(this.manifestFile, reason);

    /// <summary>
    /// 在插件重新加载前调用。
    /// </summary>
    protected virtual void OnPreReload()
    {
    }

    /// <summary>创建插件的新实例。</summary>
    /// <param name="manifest">插件清单。</param>
    /// <param name="scope">服务作用域。</param>
    /// <param name="type">插件主类的类型。</param>
    /// <param name="dalamudInterface"><see cref="IDalamudPluginInterface"/> 的实例。</param>
    /// <returns>插件的新实例。</returns>
    private static async Task<IDalamudPlugin> CreatePluginInstance(
        LocalPluginManifest manifest,
        IServiceScope scope,
        Type type,
        DalamudPluginInterface dalamudInterface)
    {
        var framework = await Service<Framework>.GetAsync();
        var forceFrameworkThread = manifest.LoadSync && manifest.LoadRequiredState is 0 or 1;
        var newInstanceTask = forceFrameworkThread ? framework.RunOnFrameworkThread(Create) : Create();
        return await newInstanceTask.ConfigureAwait(false);

        async Task<IDalamudPlugin> Create() => (IDalamudPlugin)await scope.CreateAsync(type, dalamudInterface);
    }

    private static void SetupLoaderConfig(LoaderConfig config)
    {
        config.IsUnloadable = true;
        config.LoadInMemory = true;
        config.PreferSharedTypes = false;

        // 确保插件不加载自己的 Dalamud 程序集。
        // 我们不递归固定这个；如果插件加载自己的 Dalamud 程序集，这总是错误的，
        // 但插件可能加载 Dalamud 依赖的其他程序集的其他版本。
        config.SharedAssemblies.Add((typeof(EntryPoint).Assembly.GetName(), false));
        config.SharedAssemblies.Add((typeof(Common.DalamudStartInfo).Assembly.GetName(), false));

        // 固定 Lumina，因为我们将其作为 API 表面公开。在有人再次删除这个之前，请参阅 #1598。
        // 对 Lumina 的更改应该尽可能地上游，如果有人想重新添加未固定的 Lumina，
        // 我们需要将其置于某种功能标志之后。
        config.SharedAssemblies.Add((typeof(Lumina.GameData).Assembly.GetName(), true));
        config.SharedAssemblies.Add((typeof(Lumina.Excel.Sheets.Addon).Assembly.GetName(), true));
    }

    private void EnsureLoader()
    {
        if (this.loader != null)
            return;

        try
        {
            this.loader = PluginLoader.CreateFromAssemblyFile(this.DllFile.FullName, SetupLoaderConfig);
        }
        catch (InvalidOperationException ex)
        {
            Log.Error(ex, "Loader.CreateFromAssemblyFile() 失败");
            this.State = PluginState.DependencyResolutionFailed;
            throw;
        }

        try
        {
            this.pluginAssembly = this.loader.LoadDefaultAssembly();
        }
        catch (Exception ex)
        {
            this.pluginAssembly = null;
            this.pluginType = null;
            this.loader.Dispose();

            Log.Error(ex, $"不是插件: {this.DllFile.FullName}");
            throw new InvalidPluginException(this.DllFile);
        }

        try
        {
            this.pluginType = this.pluginAssembly.GetTypes().FirstOrDefault(type => type.IsAssignableTo(typeof(IDalamudPlugin)));
        }
        catch (ReflectionTypeLoadException ex)
        {
            Log.Error(ex, $"在搜索 IDalamudPlugin 时无法加载一个或多个类型: {this.DllFile.FullName}");
            // 在解析类型时发生了一些错误，但我们仍然想寻找 IDalamudPlugin。让 Load() 处理错误。
            this.pluginType = ex.Types.FirstOrDefault(type => type != null && type.IsAssignableTo(typeof(IDalamudPlugin)));
        }

        if (this.pluginType == default)
        {
            this.pluginAssembly = null;
            this.pluginType = null;
            this.loader.Dispose();

            Log.Error($"没有任何类型继承自 IDalamudPlugin: {this.DllFile.FullName}");
            throw new InvalidPluginException(this.DllFile);
        }
    }

    /// <summary>清除并处置与插件实例相关联的所有资源。</summary>
    /// <param name="disposalMode">是否清除并处置 <see cref="loader"/>。</param>
    /// <returns>如果发生任何异常。</returns>
    private async Task<AggregateException?> ClearAndDisposeAllResources(PluginLoaderDisposalMode disposalMode)
    {
        List<Exception>? exceptions = null;
        Log.Verbose(
            "{name}({id}): {fn}(disposalMode={disposalMode})",
            this.InternalName,
            this.EffectiveWorkingPluginId,
            nameof(this.ClearAndDisposeAllResources),
            disposalMode);

        // 首先清除插件实例。
        if (!await AttemptCleanup(
            nameof(this.instance),
            Interlocked.Exchange(ref this.instance, null),
            this.manifest,
            static async (inst, manifest) =>
            {
                var framework = Service<Framework>.GetNullable();
                if (manifest.CanUnloadAsync || framework is null)
                    inst.Dispose();
                else
                    await framework.RunOnFrameworkThread(inst.Dispose).ConfigureAwait(false);
            }))
        {
            // 插件未加载；加载器无论如何都不会被引用，所以不需要等待。
            disposalMode = PluginLoaderDisposalMode.ImmediateDispose;
        }

        // 下面的字段预期在插件被（尝试）处置之前保持活动状态。
        // 在此点之后清除它们。
        this.pluginType = null;
        this.pluginAssembly = null;

        await AttemptCleanup(
            nameof(this.serviceScope),
            Interlocked.Exchange(ref this.serviceScope, null),
            0,
            static (x, _) => x.DisposeAsync());

        await AttemptCleanup(
            nameof(this.dalamudInterface),
            Interlocked.Exchange(ref this.dalamudInterface, null),
            0,
            static (x, _) =>
            {
                x.Dispose();
                return ValueTask.CompletedTask;
            });

        if (disposalMode != PluginLoaderDisposalMode.None)
        {
            await AttemptCleanup(
                nameof(this.loader),
                Interlocked.Exchange(ref this.loader, null),
                disposalMode == PluginLoaderDisposalMode.WaitBeforeDispose
                    ? Service<DalamudConfiguration>.Get().PluginWaitBeforeFree ??
                      PluginManager.PluginWaitBeforeFreeDefault
                    : 0,
                static async (ldr, waitBeforeDispose) =>
                {
                    // 以防插件仍然有它们在本应该取消时没有取消的运行任务，
                    // 给它们一些时间来完成它。
                    // 这有助于避免重新加载的插件与自己之前的实例发生冲突。
                    await Task.Delay(waitBeforeDispose);

                    ldr.Dispose();
                });
        }

        return exceptions is not null
                   ? (AggregateException)ExceptionDispatchInfo.SetCurrentStackTrace(new AggregateException(exceptions))
                   : null;

        async ValueTask<bool> AttemptCleanup<T, TContext>(
            string name,
            T? what,
            TContext context,
            Func<T, TContext, ValueTask> cb)
            where T : class
        {
            if (what is null)
                return false;

            try
            {
                await cb.Invoke(what, context);
                Log.Verbose("{name}({id}): {what} 已处置", this.InternalName, this.EffectiveWorkingPluginId, name);
            }
            catch (Exception ex)
            {
                exceptions ??= [];
                exceptions.Add(ex);
                Log.Error(
                    ex,
                    "{name}({id}): 处置 {what} 失败",
                    this.InternalName,
                    this.EffectiveWorkingPluginId,
                    name);
            }

            return true;
        }
    }
}
