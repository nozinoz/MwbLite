// MwbLite compatibility shim.
//
// Upstream InputHook.cs, InputSimulation.cs and Common.cs (input hook / injection code that MwbLite keeps
// unchanged) import Microsoft.PowerToys.Settings.UI.Library for its HotkeySettings type. This file provides
// just the members they use, in the same namespace, so those files compile without the PowerToys Settings
// library. Behaviour mirrors upstream src/settings-ui/Settings.UI.Library/HotkeySettings.cs (data only).
namespace Microsoft.PowerToys.Settings.UI.Library
{
    public sealed class HotkeySettings
    {
        public HotkeySettings()
        {
        }

        public HotkeySettings(bool win, bool ctrl, bool alt, bool shift, int code)
        {
            Win = win;
            Ctrl = ctrl;
            Alt = alt;
            Shift = shift;
            Code = code;
        }

        public bool Win { get; set; }

        public bool Ctrl { get; set; }

        public bool Alt { get; set; }

        public bool Shift { get; set; }

        public int Code { get; set; }

        public bool IsEmpty()
        {
            return !Win && !Ctrl && !Alt && !Shift && Code == 0;
        }

        public override string ToString()
        {
            if (IsEmpty())
            {
                return string.Empty;
            }

            string s = string.Empty;
            s += Win ? "Win+" : string.Empty;
            s += Ctrl ? "Ctrl+" : string.Empty;
            s += Alt ? "Alt+" : string.Empty;
            s += Shift ? "Shift+" : string.Empty;
            return s + (Code > 0 ? ((System.Windows.Forms.Keys)Code).ToString() : string.Empty);
        }
    }
}
