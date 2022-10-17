using Dalamud.Configuration;
using Dalamud.Plugin;
using System;

namespace EldenRing
{
    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        public enum DeathSfxType
        {
            Malenia,
            Old
        }
        
        public int Version { get; set; } = 0;

        public float Volume { get; set; } = 1;
        
        public bool ShowCraftFailed { get; set; } = true;

        public bool ShowEnemyFelled { get; set; } = true;

        public bool ShowDeath { get; set; } = true;

        public bool ShowIntro { get; set; } = true;

        public DeathSfxType DeathSfx { get; set; } = DeathSfxType.Old;

        // the below exist just to make saving less cumbersome

        [NonSerialized]
        private DalamudPluginInterface? pluginInterface;

        public void Initialize(DalamudPluginInterface pluginInterface)
        {
            this.pluginInterface = pluginInterface;
        }

        public void Save()
        {
            pluginInterface!.SavePluginConfig(this);
        }
    }
}
