using ImGuiNET;
using System;
using System.Numerics;

namespace EldenRing
{
    // It is good to have this be disposable in general, in case you ever need it
    // to do any cleanup
    class PluginUI : IDisposable
    {
        private Configuration config;
        
        private bool settingsVisible = false;
        public bool SettingsVisible
        {
            get { return this.settingsVisible; }
            set { this.settingsVisible = value; }
        }

        // passing in the image here just for simplicity
        public PluginUI(Configuration configuration)
        {
            this.config = configuration;
        }

        public void Dispose()
        {
        }

        public void Draw()
        {
            // This is our only draw handler attached to UIBuilder, so it needs to be
            // able to draw any windows we might have open.
            // Each method checks its own visibility/state to ensure it only draws when
            // it actually makes sense.
            // There are other ways to do this, but it is generally best to keep the number of
            // draw delegates as low as possible.

            //DrawMainWindow();
            DrawSettingsWindow();
        }

        /*public void DrawMainWindow()
        {
            if (!Visible)
            {
                return;
            }

            ImGui.SetNextWindowSize(new Vector2(375, 330), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(375, 330), new Vector2(float.MaxValue, float.MaxValue));
            if (ImGui.Begin("My Amazing Window", ref this.visible, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                ImGui.Text($"The random config bool is {this.configuration.SomePropertyToBeSavedAndWithADefault}");

                if (ImGui.Button("Show Settings"))
                {
                    SettingsVisible = true;
                }

                ImGui.Spacing();

                ImGui.Text("Have a goat:");
                ImGui.Indent(55);
                ImGui.Image(this.goatImage.ImGuiHandle, new Vector2(this.goatImage.Width, this.goatImage.Height));
                ImGui.Unindent(55);
            }
            ImGui.End();
        }*/

        public void ToggleSettings()
        {
            SettingsVisible = !SettingsVisible;
        }

        public void DrawSettingsWindow()
        {
            if (!SettingsVisible)
            {
                return;
            }

            ImGui.SetNextWindowSize(new Vector2(232, 150), ImGuiCond.Always);
            if (ImGui.Begin("Eldenring Plugin Config", ref this.settingsVisible,
                ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                // can't ref a property, so use a local copy
                var configValue = this.config.ShowEnemyFelled;
                if (ImGui.Checkbox("Show Enemy Killed", ref configValue))
                {
                    this.config.ShowEnemyFelled = configValue;
                    // can save immediately on change, if you don't want to provide a "Save and Close" button
                    this.config.Save();
                }
                
                configValue = this.config.ShowCraftFailed;
                if (ImGui.Checkbox("Show Craft Failed", ref configValue))
                {
                    this.config.ShowCraftFailed = configValue;
                    // can save immediately on change, if you don't want to provide a "Save and Close" button
                    this.config.Save();
                }
                
                configValue = this.config.ShowEnemyFelled;
                if (ImGui.Checkbox("Show Death", ref configValue))
                {
                    this.config.ShowDeath = configValue;
                    // can save immediately on change, if you don't want to provide a "Save and Close" button
                    this.config.Save();
                }
                
                configValue = this.config.ShowIntro;
                if (ImGui.Checkbox("Combat start Sfx", ref configValue))
                {
                    this.config.ShowIntro = configValue;
                    // can save immediately on change, if you don't want to provide a "Save and Close" button
                    this.config.Save();
                }

                var value = (int)this.config.DeathSfx;
                if (ImGui.Combo("Death Sfx", ref value, new[] {"Malenia", "Old"}, 2))
                {
                    this.config.DeathSfx = (Configuration.DeathSfxType) value;
                    this.config.Save();
                }
            }
            ImGui.End();
        }
    }
}
