using BepInEx;
using BepInEx.Configuration;

namespace Blackout
{
    [BepInPlugin("com.vultify.blackout", "Blackout", "1.0.0")]
    public class BlackoutPlugin : BaseUnityPlugin
    {
        private ConfigEntry<bool> _modEnabled;

        private void Awake()
        {
            _modEnabled = Config.Bind(
                "1. General",
                "Enable Mod",
                true,
                "Master toggle — enables or disables the entire mod");
        }
    }
}
