namespace ZodiacBuddy.Stages.Atma;

/// <summary>
/// Configuration class for Atma relic.
/// </summary>
public class AtmaConfiguration {
    
    public bool Enabled { get; set; } = true;
    
    public bool BraveCopyTarget { get; set; } = true;
    
    public bool BraveEchoTarget { get; set; } = true;
    
    public bool DisplayAtmaWindow { get; set; }
    
    public bool DisableTeleport { get; set; }
}
