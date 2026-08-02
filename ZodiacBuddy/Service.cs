using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ZodiacBuddy.BonusLight;
using ZodiacBuddy.Stages.Atma;

namespace ZodiacBuddy;

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
public class Service {
    [PluginService] public static IDalamudPluginInterface Interface { get; set; } = null!;
    [PluginService] public static IChatGui ChatGui { get; set; } = null!;
    [PluginService] public static IClientState ClientState { get; set; } = null!;
    [PluginService] public static IDutyState DutyState { get; set; } = null!;
    [PluginService] public static ICommandManager CommandManager { get; set; } = null!;
    [PluginService] public static IFateTable FateTable { get; set; } = null!;
    [PluginService] public static IDataManager DataManager { get; set; } = null!;
    [PluginService] public static IFramework Framework { get; set; } = null!;
    [PluginService] public static IGameGui GameGui { get; set; } = null!;
    [PluginService] public static IToastGui Toasts { get; set; } = null!;
    [PluginService] public static IPluginLog PluginLog { get; set; } = null!;
    [PluginService] public static IAddonLifecycle AddonLifecycle { get; set; } = null!;
    
    [PluginService] public static IObjectTable ObjectTable { get; set; } = null!;
    [PluginService] public static ITargetManager TargetManager { get; set; } = null!;
    [PluginService] public static IGameInventory GameInventory { get; set; } = null!;
    public static ZodiacBuddyPlugin Plugin { get; set; } = null!;
    public static PluginConfiguration Configuration { get; set; } = null!;
    public static AtmaManager AtmaManager { get; set; } = null!;
    public static BonusLightManager BonusLightManager { get; set; } = null!;
}
