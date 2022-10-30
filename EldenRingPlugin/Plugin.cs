using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Network;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface;
using Dalamud.Interface.Animation;
using Dalamud.Interface.Animation.EasingFunctions;
using Dalamud.Memory;
using Dalamud.Utility;
using ImGuiNET;
using ImGuiScene;
using Lumina.Excel.GeneratedSheets;

using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Dalamud.Logging;
using EldenRing.Audio;
using Lumina.Excel;

namespace EldenRing
{
    public class EldenRing : IDalamudPlugin
    {
        public string Name => "Elden Ring April Fools";

        private const string CommandName = "/eldenring";

        private readonly TextureWrap erDeathBgTexture;
        private readonly TextureWrap erNormalDeathTexture;
        private readonly TextureWrap erCraftFailedTexture;
        private readonly TextureWrap erEnemyFelledTexture;

        private readonly ExcelSheet<TerritoryType> territories;
        private readonly string synthesisFailsMessage;

        private readonly Stopwatch time = new();

        private AudioHandler AudioHandler { get; }
        private PluginUI PluginUI { get; }
        private Configuration Config { get; }
        
        private PluginAddressResolver address;
        
        private AnimationState currentState = AnimationState.NotPlaying;
        private AnimationType currentAnimationType = AnimationType.Death;

        private Easing alphaEasing;
        private Easing scaleEasing;
        
        private int msFadeInTime = 1000;
        private int msFadeOutTime = 2000;
        private int msWaitTime = 1600;

        private int musicChangeCounter = 0;
        private int songChangeCounter = 0;

        private readonly Hook<SetGlobalBgmDelegate> setGlobalBgmHook;
        private readonly Hook<ActionIntegrityDelegate> actionIntegrityDelegateHook;


        private TextureWrap TextTexture => this.currentAnimationType switch
        {
            AnimationType.Death => this.erNormalDeathTexture,
            AnimationType.CraftFailed => this.erCraftFailedTexture,
            AnimationType.EnemyFelled => this.erEnemyFelledTexture,
            _ => throw new ArgumentOutOfRangeException()
        };

        private AudioTrigger DeathSfx => Config.DeathSfx switch
        {
            Configuration.DeathSfxType.Malenia => AudioTrigger.Malenia,
            Configuration.DeathSfxType.Old => AudioTrigger.Death,
            _ => throw new ArgumentOutOfRangeException()
        };

        private enum AnimationState
        {
            NotPlaying,
            FadeIn,
            Wait,
            FadeOut,
        }
        
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct AddStatusEffect {
            private uint Unknown1;
            private uint RelatedActionSequence;
            private uint ActorId;
            private uint CurrentHp;
            private uint MaxHp;
            private ushort CurrentMp;
            private ushort Unknown3;
            private byte DamageShield;
            public byte EffectCount;
            private ushort Unknown6;

            public unsafe fixed byte Effects[64];
        }
        
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct StatusEffectAddEntry {
            public byte EffectIndex;
            public byte Unknown1;
            public ushort EffectId;
            public ushort Unknown2;
            public ushort Unknown3;
            public float Duration;
            public uint SourceActorId;
        }

        // ReSharper disable UnusedMember.Local
        private enum DirectorUpdateType : uint
        {
            DutyInit = 0x40000001, //  params: 3: duty length in seconds, eg 0xE10 is 3600 or 1 hour, 4-6: unused
            DutyComplete = 0x40000002, // "Duty Complete" flying text
            DutyClear = 0x40000003, // Emits when a piece of content is 'cleared'
            DutyTimeSync = 0x40000004, // The duty time in seconds
            DutyFadeout = 0x40000005, // Instructs the screen to fade to black, used on group wipe
            DutyRecommence = 0x40000006, // When duty is restarted
            DutySetFlag = 0x40000007, // Sets some internal boolean flags in director
            DutyInitVote = 0x40000008, // init vote (ie abandon, kick, etc) – params: 3: vote type, 4: vote initiator, 5-6: unknown
            DutyConcludeVote = 0x40000009, // conclude vote – params: 3: vote type, 4: 1 for succeed/0 for fail, 5: vote initiator, 6: unknown
            DutyPartyInvite = 0x4000000A, // seems to be something related to party invites
            DutyGate = 0x4000000C, // This command sets the state of artificial walls in duties, such as gates in Doma Castle or water walls in Neverreap
            DutyNewPlayer = 0x4000000D, // This command comes in when one or more members in the instance are new to the duty
            DutyLevelUp = 0x4000000E, // This command comes in when a player levels up in the duty
            DutyFadeIn = 0x40000010, // Causes a fade-in to happen
            DutyBarrierUp = 0x40000012, // Puts an 'instance' barrier up
            /*
            * This command lists how many party members are eligible for duty rewards. The number of chests dropped in locked content
            * appears to be hardcoded into the client, and as such doesn't change this packet.
            */
            DutyParyLoot = 0x40000013,

            MusicChange = 0x80000001, // When background music changes in duties, i.e. boss music in dungeons
            InstanceTimeSync = 0x80000002,
        }

        private enum AnimationType
        {
            Death,
            CraftFailed,
            EnemyFelled,
            CombatIntro
        }

        private enum ContentType : uint
        {
            Dungeon = 2,
            Trial = 4,
            Raid = 5,
            Ultimate = 28
        }
        
        public EldenRing(DalamudPluginInterface pluginInterface)
        {
            pluginInterface.Create<Service>();

            Config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            Config.Initialize(pluginInterface);

            this.address = new PluginAddressResolver();
            this.address.Setup(Service.SigScanner);
            this.setGlobalBgmHook = Hook<SetGlobalBgmDelegate>.FromAddress(this.address.SetGlobalBGM, this.HandleSetGlobalBgmDetour);
            this.setGlobalBgmHook.Enable();
            this.actionIntegrityDelegateHook =
                Hook<ActionIntegrityDelegate>.FromAddress(this.address.ActionIntegrity,
                    this.ActionIntegrityDelegateDetour);
            this.actionIntegrityDelegateHook.Enable();

            PluginUI = new PluginUI(Config);
            erDeathBgTexture = pluginInterface.UiBuilder.LoadImage(Path.Combine(pluginInterface.AssemblyLocation.Directory?.FullName!, "er_death_bg.png"))!;
            erNormalDeathTexture = pluginInterface.UiBuilder.LoadImage(Path.Combine(pluginInterface.AssemblyLocation.Directory?.FullName!, "er_normal_death.png"))!;
            erCraftFailedTexture = pluginInterface.UiBuilder.LoadImage(Path.Combine(pluginInterface.AssemblyLocation.Directory?.FullName!, "er_craft_failed.png"))!;
            erEnemyFelledTexture = pluginInterface.UiBuilder.LoadImage(Path.Combine(pluginInterface.AssemblyLocation.Directory?.FullName!, "er_enemy_felled.png"))!;

            AudioHandler = new();
            AudioHandler.LoadSound(AudioTrigger.Death, Path.Combine(pluginInterface.AssemblyLocation.Directory?.FullName!, "snd_death_er.wav"));
            AudioHandler.LoadSound(AudioTrigger.Malenia, Path.Combine(pluginInterface.AssemblyLocation.Directory?.FullName!, "snd_malenia_death_er.wav"));
            AudioHandler.LoadSound(AudioTrigger.MaleniaKilled, Path.Combine(pluginInterface.AssemblyLocation.Directory?.FullName!, "snd_enemy_felled_er.wav"));
            AudioHandler.LoadSound(AudioTrigger.MaleniaIntro, Path.Combine(pluginInterface.AssemblyLocation.Directory?.FullName!, "snd_malenia_intro_er.wav"));
            

            if (erDeathBgTexture == null || erNormalDeathTexture == null || erCraftFailedTexture == null)
            {
                PluginLog.Error("Failed to load images");
                return;
            }

            AudioHandler.Volume = this.Config.Volume;
            int vol = (int)(this.Config.Volume * 100f);
            PluginLog.Debug($"Volume set to {vol}%");

            Service.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Used to control the volume of the audio using \"vol 0-100\""
            });

            synthesisFailsMessage = Service.DataManager.GetExcelSheet<LogMessage>()!.GetRow(1160)!.Text.ToDalamudString().TextValue;
            territories = Service.DataManager.GetExcelSheet<TerritoryType>()!;
            
            pluginInterface.UiBuilder.Draw += Draw;
            pluginInterface.UiBuilder.Draw += PluginUI.Draw;
            pluginInterface.UiBuilder.OpenConfigUi += PluginUI.ToggleSettings;
            Service.ChatGui.ChatMessage += ChatGuiOnChatMessage;
            Service.GameNetwork.NetworkMessage += GameNetworkOnNetworkMessage;
            Service.Condition.ConditionChange += ConditionOnChanged;
        }
        
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate IntPtr SetGlobalBgmDelegate(ushort bgmKey, byte a2, uint a3, uint a4, uint a5, byte a6);
        
        private delegate void ActionIntegrityDelegate(uint targetId, IntPtr actionIntegrityData, bool isReplay);

        
        private IntPtr HandleSetGlobalBgmDetour(ushort bgmKey, byte a2, uint a3, uint a4, uint a5, byte a6)
        {
            var retVal = this.setGlobalBgmHook.Original(bgmKey, a2, a3, a4, a5, a6);
            
            if (Config.ShowDebug)
            {
                Service.ChatGui.Print($"SetGlobalBGM {bgmKey}");
                songChangeCounter++;
                if (musicChangeCounter >= 3)
                {
                    if (Config.ShowDebug)
                    {
                        Service.ChatGui.Print("Malenia Intro");
                    }

                    if (!this.AudioHandler.IsPlaying())
                    {
                        this.AudioHandler.PlaySound(AudioTrigger.MaleniaIntro);
                    }
                } 
            }
            
            return retVal;
        }
        
        private unsafe void ActionIntegrityDelegateDetour(uint targetId, IntPtr actionIntegrityData, bool isReplay) {
            actionIntegrityDelegateHook.Original(targetId, actionIntegrityData, isReplay);
            
            try {
                var message = (AddStatusEffect*)actionIntegrityData;

                /*if (Service.ObjectTable.SearchById(targetId) is not PlayerCharacter p)
                    return;*/
                if (targetId != Service.ClientState.LocalPlayer?.ObjectId)
                    return;

                var effects = (StatusEffectAddEntry*)message->Effects;
                var effectCount = Math.Min(message->EffectCount, 4u);
                for (uint j = 0; j < effectCount; j++) {
                    var effect = effects[j];
                    var effectId = effect.EffectId;
                    if (effectId <= 0)
                        continue;
                    if (effect.Duration < 0)
                        continue;
                    var status = Service.DataManager.Excel.GetSheet<Status>()?.GetRow(effectId);
                    if (status?.Icon is 17101 or 15020)
                    {
                        if (Config.ShowDebug)
                        {
                            Service.ChatGui.Print("Emotional Damage!");
                        }
                    }
                }
            } catch (Exception e) {
                PluginLog.Error(e, "Caught unexpected exception");
            }
        }

        private unsafe void GameNetworkOnNetworkMessage(IntPtr dataptr, ushort opcode, uint sourceactorid, uint targetactorid, NetworkMessageDirection direction)
        {
            if (!(Config.ShowEnemyFelled || Config.ShowIntro))
                return;
            
            var dataManager = Service.DataManager;
            if (opcode != dataManager.ServerOpCodes["ActorControlSelf"]) // pull the opcode from Dalamud's definitions
                return;
            
            var cat = *(ushort*)(dataptr + 0x00);
            var instance = *(uint*) (dataptr + 0x04);
            var updateType = *(uint*)(dataptr + 0x08);
            
            if (cat == 0x6D)
            {
                if (Config.ShowDebug)
                {
                    var name = Enum.GetName(typeof(DirectorUpdateType), updateType) ?? "unknown";
                    Service.ChatGui.Print($"Director update: {name} ({updateType:x8})");
                }

                if (updateType == (uint) DirectorUpdateType.DutyClear && Config.ShowEnemyFelled)
                {
                    Task.Delay(1000).ContinueWith(_ =>
                    {
                        this.PlayAnimation(AnimationType.EnemyFelled);
                        if (this.AudioHandler.IsPlaying())
                            return;
                        this.AudioHandler.PlaySound(AudioTrigger.MaleniaKilled);
                    }); 
                }
                if (updateType == (uint) DirectorUpdateType.MusicChange && IsDungeon() && Config.ShowIntro)
                {
                    musicChangeCounter++;
                    PluginLog.Verbose($"musicChangeCounter: {musicChangeCounter}");
                }
                if (updateType == (uint?) DirectorUpdateType.DutyInit && IsDungeon())
                {
                    musicChangeCounter = 0;
                    songChangeCounter = 0;
                    PluginLog.Verbose($"reset musicChangeCounter: {musicChangeCounter}");
                }
            }
        }
        
        
        private void ChatGuiOnChatMessage(XivChatType type, uint senderid, ref SeString sender, ref SeString message, ref bool ishandled)
        {
            if (Config.ShowCraftFailed && message.TextValue.Contains(this.synthesisFailsMessage))
            {
                this.PlayAnimation(AnimationType.CraftFailed);
                PluginLog.Verbose("Craft failed");
            }
        }

        private void ConditionOnChanged(ConditionFlag flag, bool value)
        {
            if (Config.ShowIntro && Service.Condition[ConditionFlag.BoundByDuty] 
                                 && flag == ConditionFlag.InCombat && value && Is8ManDuty())
            {
                if (Config.ShowDebug)
                {
                    Service.ChatGui.Print("Malenia Intro");
                }

                if (!this.AudioHandler.IsPlaying())
                {
                    this.AudioHandler.PlaySound(AudioTrigger.MaleniaIntro);
                }
            }
            if (Config.ShowDeath && flag == ConditionFlag.Unconscious && value)
            {
                PlayAnimation(AnimationType.Death);
                if (CheckIsSfxEnabled())
                {
                    AudioHandler.PlaySound(DeathSfx);
                }
                PluginLog.Verbose($"Player died {value}");
            }
        }

        private void Draw()
        {
            var vpSize = ImGuiHelpers.MainViewport.Size;

            ImGui.SetNextWindowPos(new Vector2(0, 0), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(vpSize.X, vpSize.Y), ImGuiCond.Always);
            ImGuiHelpers.ForceNextWindowMainViewport();

            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
            ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0, 0, 0, 0));
            ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0, 0, 0, 0));
            ImGui.PushStyleColor(ImGuiCol.BorderShadow, new Vector4(0, 0, 0, 0));

            if (ImGui.Begin("fools22", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus
                                       | ImGuiWindowFlags.NoMouseInputs | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoScrollbar))
            {
                if (this.currentState != AnimationState.NotPlaying)
                {
                    this.alphaEasing.Update();
                    this.scaleEasing.Update();
                }

                switch (this.currentState)
                {
                    case AnimationState.FadeIn:
                        this.FadeIn(vpSize);
                        break;
                    case AnimationState.Wait:
                        this.Wait(vpSize);
                        break;
                    case AnimationState.FadeOut:
                        this.FadeOut(vpSize);
                        break;
                }
            }

            ImGui.End();

            ImGui.PopStyleColor(3);
            ImGui.PopStyleVar(3);
        }

        private static void AdjustCursorAndDraw(Vector2 vpSize, TextureWrap tex, float scale = 1.0f)
        {
            ImGui.SetCursorPos(new Vector2(0, 0));

            var width = vpSize.X;
            var height = tex.Height / (float)tex.Width * width;

            if (height < vpSize.Y)
            {
                height = vpSize.Y;
                width = tex.Width / (float)tex.Height * height;
            }

            var scaledSize = new Vector2(width, height) * scale;
            var difference = scaledSize - vpSize;

            var cursor = ImGui.GetCursorPos();
            ImGui.SetCursorPos(cursor - (difference / 2));

            ImGui.Image(tex.ImGuiHandle, scaledSize);
        }

        private void FadeIn(Vector2 vpSize)
        {
            if (this.time.ElapsedMilliseconds > this.msFadeInTime)
                this.currentState = AnimationState.Wait;

            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, (float)this.alphaEasing.Value);

            AdjustCursorAndDraw(vpSize, this.erDeathBgTexture);
            AdjustCursorAndDraw(vpSize, this.TextTexture, this.scaleEasing.EasedPoint.X);

            ImGui.PopStyleVar();
        }

        private void Wait(Vector2 vpSize)
        {
            if (this.time.ElapsedMilliseconds > this.msFadeInTime + this.msWaitTime)
            {
                this.currentState = AnimationState.FadeOut;
                this.alphaEasing = new InOutCubic(TimeSpan.FromMilliseconds(this.msFadeOutTime));
                this.alphaEasing.Start();
            }

            AdjustCursorAndDraw(vpSize, this.erDeathBgTexture);
            AdjustCursorAndDraw(vpSize, this.TextTexture, this.scaleEasing.EasedPoint.X);
        }

        private void FadeOut(Vector2 vpSize)
        {
            if (this.time.ElapsedMilliseconds > this.msFadeInTime + this.msWaitTime + this.msFadeOutTime)
            {
                this.currentState = AnimationState.NotPlaying;
                this.time.Stop();
            }

            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 1 - (float)this.alphaEasing.Value);

            AdjustCursorAndDraw(vpSize, this.erDeathBgTexture);
            AdjustCursorAndDraw(vpSize, this.TextTexture, this.scaleEasing.EasedPoint.X);

            ImGui.PopStyleVar();
        }

        private void PlayAnimation(AnimationType type)
        {

            if (this.currentState != AnimationState.NotPlaying)
                return;

            this.currentAnimationType = type;

            this.currentState = AnimationState.FadeIn;
            this.alphaEasing = new InOutCubic(TimeSpan.FromMilliseconds(this.msFadeInTime));
            this.alphaEasing.Start();

            this.scaleEasing = new OutCubic(TimeSpan.FromMilliseconds(this.msFadeInTime + this.msWaitTime + this.msFadeOutTime))
            {
                Point1 = new Vector2(0.95f, 0.95f),
                Point2 = new Vector2(1.05f, 1.05f),
            };
            this.scaleEasing.Start();

            this.time.Reset();
            this.time.Start();
        }

        private unsafe bool CheckIsSfxEnabled()
        {
            try
            {
                var framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
                var configBase = framework->SystemConfig.CommonSystemConfig.ConfigBase;

                var seEnabled = false;
                var masterEnabled = false;

                for (var i = 0; i < configBase.ConfigCount; i++)
                {
                    var entry = configBase.ConfigEntry[i];

                    if (entry.Name != null)
                    {
                        var name = MemoryHelper.ReadStringNullTerminated(new IntPtr(entry.Name));

                        if (name == "IsSndSe")
                        {
                            var value = entry.Value.UInt;
                            PluginLog.Verbose("{Name} - {Type} - {Value}", name, entry.Type, value);

                            seEnabled = value == 0;
                        }

                        if (name == "IsSndMaster")
                        {
                            var value = entry.Value.UInt;
                            PluginLog.Verbose("{Name} - {Type} - {Value}", name, entry.Type, value);

                            masterEnabled = value == 0;
                        }
                    }
                }

                return seEnabled && masterEnabled;
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Error checking if sfx is enabled");
                return true;
            }
        }

        private uint? GetContentType()
        {
            var territory = territories.GetRow(Service.ClientState.TerritoryType);
            return territory?.ContentFinderCondition.Value?.ContentType.Row;
        }

        private bool IsDungeon()
        {
            return GetContentType() == (uint) ContentType.Dungeon;
        }

        private bool Is8ManDuty()
        {
            return GetContentType() == (uint) ContentType.Trial || GetContentType() == (uint) ContentType.Raid;
        }


        public void Dispose()
        {
            Service.Interface.UiBuilder.Draw -= Draw;
            Service.Interface.UiBuilder.Draw -= PluginUI.Draw;
            Service.Interface.UiBuilder.OpenConfigUi -= PluginUI.ToggleSettings;
            Service.ChatGui.ChatMessage -= ChatGuiOnChatMessage;
            Service.GameNetwork.NetworkMessage -= GameNetworkOnNetworkMessage;
            Service.Condition.ConditionChange -= ConditionOnChanged;

            actionIntegrityDelegateHook.Dispose();
            setGlobalBgmHook.Dispose();
            erDeathBgTexture.Dispose();
            erNormalDeathTexture.Dispose();
            erCraftFailedTexture.Dispose();
            PluginUI.Dispose();
            Config.Save();

            Service.CommandManager.RemoveHandler(CommandName);
        }

        private void SetVolume(string vol)
        {
            try
            {
                var newVol = int.Parse(vol) / 100f;
                PluginLog.Debug($"{Name}: Setting volume to {newVol}");
                AudioHandler.Volume = newVol;
                Config.Volume = newVol;
                Service.ChatGui.Print($"Volume set to {vol}%");
            }
            catch (Exception)
            {
                Service.ChatGui.PrintError("Please use a number between 0-100");
            }
        }

        private void OnCommand(string command, string args)
        {
            PluginLog.Debug("{Command} - {Args}", command, args);
            var argList = args.Split(' ');

            PluginLog.Debug(argList.Length.ToString());

            if (argList.Length == 0)
                return;


            // TODO: This is super rudimentary (garbage) argument parsing. Make it better
            switch (argList[0])
            {
                case "vol":
                    if (argList.Length != 2) return;
                    SetVolume(argList[1]);
                    break;
                case "":
                    // in response to the slash command, just display our main ui
                    PluginUI.SettingsVisible = true;
                    Service.ChatGui.PrintError("Please use \"/eldenring vol <num>\" to control volume");
                    break;
            }
        }
    }
}
