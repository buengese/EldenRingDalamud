using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
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
        
        private AnimationState currentState = AnimationState.NotPlaying;
        private AnimationType currentAnimationType = AnimationType.Death;

        private Easing alphaEasing;
        private Easing scaleEasing;

        private bool lastFrameUnconscious;

        private int msFadeInTime = 1000;
        private int msFadeOutTime = 2000;
        private int msWaitTime = 1600;

        private int musicChangeCounter = 0;

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

        // ReSharper disable UnusedMember.Local
        private enum DirectorUpdateType : uint
        {
            DutyCommence = 0x40000001,
            DutyComplete = 0x40000003,
            DutyWipe = 0x40000005,
            DutyRecommence = 0x40000006,
            DutyUpdate = 0x40000007,
            DutyBarrierUp = 0x40000012,
            
            DungeonSetup = 0x80000000,
            MusicChange = 0x80000001,

            DungeonBossStartEnd15 = 0x80000015,
            DungeonBossStartEnd16 = 0x80000016,
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
            // Service.Framework.Update += FrameworkOnUpdate;
            Service.ChatGui.ChatMessage += ChatGuiOnChatMessage;
            Service.GameNetwork.NetworkMessage += GameNetworkOnNetworkMessage;
            Service.Condition.ConditionChange += ConditionOnChanged;
        }

        private unsafe void GameNetworkOnNetworkMessage(IntPtr dataptr, ushort opcode, uint sourceactorid, uint targetactorid, NetworkMessageDirection direction)
        {
            if (!(Config.ShowEnemyFelled || Config.ShowIntro))
                return;
            
            var dataManager = Service.DataManager;
            if (opcode != dataManager.ServerOpCodes["ActorControlSelf"]) // pull the opcode from Dalamud's definitions
                return;
            
            var cat = *(ushort*)(dataptr + 0x00);
            var updateType = *(uint*)(dataptr + 0x08);
            if (cat == 0xA1)
            {
                if (Config.ShowDebug)
                {
                    Service.ChatGui.Print($"SetBgm");
                }
            }

            if (cat == 0x6D)
            {
                if (Config.ShowDebug)
                {
                    var name = Enum.GetName(typeof(DirectorUpdateType), updateType) ?? "unknown";
                    Service.ChatGui.Print($"Director update: {name} ({updateType:x8})");
                }

                if (updateType == (uint) DirectorUpdateType.DutyComplete && Config.ShowEnemyFelled)
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
                    if (musicChangeCounter == 5)
                    {
                        PluginLog.Verbose("Malenia Intro");
                        Task.Delay(1000).ContinueWith(_ =>
                        {
                            if (this.AudioHandler.IsPlaying())
                                return;
                            this.AudioHandler.PlaySound(AudioTrigger.MaleniaIntro);
                        });
                    }
                }
                if (updateType == (uint?) DirectorUpdateType.DutyCommence && IsDungeon())
                {
                    musicChangeCounter = 0;
                    PluginLog.Verbose($"ContentType: {GetContentType()}, IsDungeon: {IsDungeon()}, Is8ManDuty: {Is8ManDuty()}");
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

        /*private void FrameworkOnUpdate(Framework framework)
        {
            var isUnconscious = Service.Condition[ConditionFlag.Unconscious];

            if (Config.ShowDeath && isUnconscious && !this.lastFrameUnconscious)
            {
                PlayAnimation(AnimationType.Death);
                if (CheckIsSfxEnabled())
                {
                    AudioHandler.PlaySound(DeathSfx);
                }
                PluginLog.Verbose($"Elden: Player died {isUnconscious}");
            }

            lastFrameUnconscious = isUnconscious;
        }*/

        private void ConditionOnChanged(ConditionFlag flag, bool value)
        {
            if (Config.ShowIntro && Service.Condition[ConditionFlag.BoundByDuty] 
                                 && flag == ConditionFlag.InCombat && value && Is8ManDuty())
            {
                PluginLog.Verbose($"ContentType: {GetContentType()}, IsDungeon: {IsDungeon()}, Is8ManDuty: {Is8ManDuty()}");
                PluginLog.Verbose("Malenia Intro");

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
            // Service.Framework.Update -= FrameworkOnUpdate;
            Service.ChatGui.ChatMessage -= ChatGuiOnChatMessage;
            Service.GameNetwork.NetworkMessage -= GameNetworkOnNetworkMessage;

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
