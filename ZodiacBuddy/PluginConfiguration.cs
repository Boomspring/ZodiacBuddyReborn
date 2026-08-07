using Dalamud.Configuration;
using Dalamud.Game.Text;
using Newtonsoft.Json;
using System.Text.Json.Serialization;
using ZodiacBuddy.BonusLight;
using ZodiacBuddy.InformationWindow;
using ZodiacBuddy.Stages.Brave;
using ZodiacBuddy.Stages.Novus;
using ZodiacBuddy.Stages.Atma;

namespace ZodiacBuddy;

public class PluginConfiguration : IPluginConfiguration {
    public int Version { get; set; } = 1;

    [JsonProperty("BraveEchoChannel")] public XivChatType ChatType { get; set; } = XivChatType.Echo;

    public AtmaConfiguration AtmaConfiguration { get; } = new();

    public BonusLightConfiguration BonusLight { get; } = new();

    public NovusConfiguration Novus { get; } = new();

    public BraveConfiguration Brave { get; } = new();

    public InformationWindowConfiguration InformationWindow { get; } = new();
    
    public void Save() => Service.Interface.SavePluginConfig(this);
}
