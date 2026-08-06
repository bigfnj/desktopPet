using System.Collections.Generic;
using DesktopPet.Modules;

namespace DesktopPet.TestModule
{
    /// <summary>Reference module for S1: subscribes to a lifecycle event and contributes UI, exercising
    /// the whole ABI (events + services + tray/options contributions) with no real behavior.</summary>
    public sealed class TestModule : IModule
    {
        private IHost _host;

        public ModuleInfo Info { get; } = new ModuleInfo
        {
            Id = "testmodule",
            Name = "Test Module",
            Version = "1.0.0",
            MinHostVersion = "1.0.0",
            Permissions = ModulePermissions.Speech,
        };

        public void Init(IHost host)
        {
            _host = host;
            host.PetPoked += OnPoked;
            host.AddTrayItems(new List<TrayItem>
            {
                new TrayItem { Label = "TestModule OK", Group = 9, Order = 0, Click = TrayClicked },
            });
            host.AddOptionsPane(new OptionsPane
            {
                Title = "Test Module",
                Schema = new List<SettingField>
                {
                    new SettingField { Id = "greeting", Label = "Greeting", Kind = SettingKind.Text },
                },
            });
        }

        private void OnPoked(PokeInfo info) { if (_host != null) _host.SayAll("poked!"); }
        private void TrayClicked() { if (_host != null) _host.SayAll("test module tray click"); }

        public void Shutdown() { if (_host != null) _host.PetPoked -= OnPoked; }
    }
}
